// Headless compute: doubles a buffer of floats on the GPU and reads the result back.
// Runs the same under the JIT (dotnet run) and as a Native AOT executable (dotnet publish).
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WGpuFury;

internal static unsafe class Program
{
    private const int Count = 1 << 20;

    private static ReadOnlySpan<byte> Shader => """
        @group(0) @binding(0) var<storage, read_write> data: array<f32>;

        @compute @workgroup_size(64)
        fn main(@builtin(global_invocation_id) id: vec3<u32>) {
            if (id.x < arrayLength(&data)) {
                data[id.x] = data[id.x] * 2.0;
            }
        }
        """u8;

    private static int Main()
    {
        Console.WriteLine($"wgpu-native {WGPU.NativeVersion} (bindings generated from {WGPU.BindingsVersion})");
        WGPU.SetLogger(WGPULogLevel.Warn, (level, message) => Console.Error.WriteLine($"[wgpu {level}] {message}"));

        using WGPUInstance instance = WGPU.wgpuCreateInstance(null);
        using WGPUAdapter adapter = instance.RequestAdapterSync(new WGPURequestAdapterOptions { PowerPreference = WGPUPowerPreference.HighPerformance });
        PrintAdapter(adapter);

        var deviceDescriptor = new WGPUDeviceDescriptor
        {
            Label = WGPUStringView.FromPinned("compute device"u8),
            UncapturedErrorCallbackInfo = new WGPUUncapturedErrorCallbackInfo { Callback = &OnUncapturedError },
        };
        using WGPUDevice device = adapter.RequestDeviceSync(instance, deviceDescriptor);
        using WGPUQueue queue = device.GetQueue();

        // Shader module: the WGSL source is chained onto the descriptor, exactly like in C.
        var wgsl = new WGPUShaderSourceWGSL { Code = WGPUStringView.FromPinned(Shader) };
        using WGPUShaderModule module = device.CreateShaderModule(new WGPUShaderModuleDescriptor { NextInChain = &wgsl.Chain });

        using WGPUComputePipeline pipeline = device.CreateComputePipeline(new WGPUComputePipelineDescriptor
        {
            Compute = new WGPUComputeState { Module = module, EntryPoint = WGPUStringView.FromPinned("main"u8) },
        });

        const ulong size = Count * sizeof(float);
        using WGPUBuffer storage = device.CreateBuffer(new WGPUBufferDescriptor
        {
            Size = size,
            Usage = WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc | WGPUBufferUsage.CopyDst,
        });
        using WGPUBuffer readback = device.CreateBuffer(new WGPUBufferDescriptor
        {
            Size = size,
            Usage = WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst,
        });

        float[] input = new float[Count];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = i;
        }

        queue.WriteBuffer(storage, 0, input.AsSpan());

        using WGPUBindGroupLayout layout = pipeline.GetBindGroupLayout(0);
        var entry = new WGPUBindGroupEntry { Binding = 0, Buffer = storage, Size = size };
        using WGPUBindGroup bindGroup = device.CreateBindGroup(new WGPUBindGroupDescriptor { Layout = layout, EntryCount = 1, Entries = &entry });

        using WGPUCommandEncoder encoder = device.CreateCommandEncoder();
        using (WGPUComputePassEncoder pass = encoder.BeginComputePass())
        {
            pass.SetPipeline(pipeline);
            pass.SetBindGroup(0, bindGroup, []);
            pass.DispatchWorkgroups(Count / 64, 1, 1);
            pass.End();
        }

        encoder.CopyBufferToBuffer(storage, 0, readback, 0, size);
        using WGPUCommandBuffer commands = encoder.Finish();
        queue.Submit([commands]);

        readback.MapSync(device, WGPUMapMode.Read, 0, (nuint)size);
        ReadOnlySpan<float> output = readback.GetConstMappedRange<float>(0, Count);
        int errors = 0;
        for (int i = 0; i < output.Length; i++)
        {
            if (output[i] != input[i] * 2)
            {
                errors++;
            }
        }

        Console.WriteLine($"output[1..4] = {output[1]}, {output[2]}, {output[3]}; last = {output[^1]}");
        readback.Unmap();

        Console.WriteLine(errors == 0 ? $"OK: {Count:N0} values doubled on the GPU." : $"FAILED: {errors} wrong values.");
        return errors == 0 ? 0 : 1;
    }

    private static void PrintAdapter(WGPUAdapter adapter)
    {
        var info = new WGPUAdapterInfo();
        if (adapter.GetInfo(&info) == WGPUStatus.Success)
        {
            Console.WriteLine($"Adapter: {info.Device} ({info.BackendType}, {info.AdapterType}) {info.Description}");
            info.FreeMembers();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUncapturedError(WGPUDevice* device, WGPUErrorType type, WGPUStringView message, void* userdata1, void* userdata2) =>
        Console.Error.WriteLine($"Uncaptured {type} error: {message}");
}
