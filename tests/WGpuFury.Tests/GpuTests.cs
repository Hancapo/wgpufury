using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WGpuFury.Tests;

/// <summary>End-to-end GPU work. Inconclusive (not failed) on machines without any adapter.</summary>
[TestClass]
public sealed unsafe class GpuTests
{
    private static WGPUInstance s_instance;
    private static WGPUAdapter s_adapter;
    private static WGPUDevice s_device;
    private static WGPUQueue s_queue;
    private static string? s_unavailable;
    private static int s_errors;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        s_instance = WGPU.wgpuCreateInstance(null);
        try
        {
            s_adapter = s_instance.RequestAdapterSync();
        }
        catch (WGPUException e)
        {
            s_unavailable = e.Message;
            return;
        }

        var descriptor = new WGPUDeviceDescriptor
        {
            UncapturedErrorCallbackInfo = new WGPUUncapturedErrorCallbackInfo { Callback = &OnError },
        };
        s_device = s_adapter.RequestDeviceSync(s_instance, descriptor);
        s_queue = s_device.GetQueue();
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        s_queue.Dispose();
        s_device.Dispose();
        s_adapter.Dispose();
        s_instance.Dispose();
    }

    [TestInitialize]
    public void RequireAdapter()
    {
        if (s_unavailable != null)
        {
            Assert.Inconclusive("No GPU adapter: " + s_unavailable);
        }

        s_errors = 0;
    }

    [TestCleanup]
    public void NoValidationErrors() => Assert.AreEqual(0, s_errors, "uncaptured WebGPU errors were reported");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnError(WGPUDevice* device, WGPUErrorType type, WGPUStringView message, void* userdata1, void* userdata2)
    {
        Interlocked.Increment(ref s_errors);
        Console.Error.WriteLine($"{type}: {message}");
    }

    [TestMethod]
    public void AdapterReportsInfoAndLimits()
    {
        var info = new WGPUAdapterInfo();
        Assert.AreEqual(WGPUStatus.Success, s_adapter.GetInfo(&info));
        Assert.IsFalse(string.IsNullOrEmpty(info.Device.ToString()));
        info.FreeMembers();

        var limits = new WGPULimits();
        Assert.AreEqual(WGPUStatus.Success, s_device.GetLimits(&limits));
        Assert.IsGreaterThanOrEqualTo(8192u, limits.MaxTextureDimension2D);
    }

    [TestMethod]
    public void ComputeRoundTrip()
    {
        const int count = 4096;
        ReadOnlySpan<byte> shader = """
            @group(0) @binding(0) var<storage, read_write> data: array<u32>;
            @compute @workgroup_size(64)
            fn main(@builtin(global_invocation_id) id: vec3<u32>) { data[id.x] = data[id.x] * 3u + 1u; }
            """u8;
        var wgsl = new WGPUShaderSourceWGSL { Code = WGPUStringView.FromPinned(shader) };
        using WGPUShaderModule module = s_device.CreateShaderModule(new WGPUShaderModuleDescriptor { NextInChain = &wgsl.Chain });
        using WGPUComputePipeline pipeline = s_device.CreateComputePipeline(new WGPUComputePipelineDescriptor
        {
            Compute = new WGPUComputeState { Module = module, EntryPoint = WGPUStringView.FromPinned("main"u8) },
        });

        const ulong size = count * sizeof(uint);
        using WGPUBuffer storage = s_device.CreateBuffer(new WGPUBufferDescriptor { Size = size, Usage = WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc | WGPUBufferUsage.CopyDst });
        using WGPUBuffer readback = s_device.CreateBuffer(new WGPUBufferDescriptor { Size = size, Usage = WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst });
        uint[] input = Enumerable.Range(0, count).Select(i => (uint)i).ToArray();
        s_queue.WriteBuffer(storage, 0, input.AsSpan());

        using WGPUBindGroupLayout layout = pipeline.GetBindGroupLayout(0);
        var entry = new WGPUBindGroupEntry { Binding = 0, Buffer = storage };
        using WGPUBindGroup group = s_device.CreateBindGroup(new WGPUBindGroupDescriptor { Layout = layout, EntryCount = 1, Entries = &entry });

        using WGPUCommandEncoder encoder = s_device.CreateCommandEncoder();
        using (WGPUComputePassEncoder pass = encoder.BeginComputePass())
        {
            pass.SetPipeline(pipeline);
            pass.SetBindGroup(0, group, []);
            pass.DispatchWorkgroups(count / 64, 1, 1);
            pass.End();
        }

        encoder.CopyBufferToBuffer(storage, 0, readback, 0, size);
        using WGPUCommandBuffer commands = encoder.Finish();
        s_queue.Submit([commands]);

        readback.MapSync(s_device, WGPUMapMode.Read, 0, (nuint)size);
        uint[] output = readback.GetConstMappedRange<uint>(0, count).ToArray();
        readback.Unmap();
        CollectionAssert.AreEqual(input.Select(x => x * 3 + 1).ToArray(), output);
    }

    [TestMethod]
    public void RenderTriangleOffscreen()
    {
        const uint width = 64, height = 64;
        ReadOnlySpan<byte> shader = """
            @vertex fn vs(@builtin(vertex_index) i: u32) -> @builtin(position) vec4<f32> {
                var p = array<vec2<f32>, 3>(vec2(-0.5, -0.5), vec2(0.5, -0.5), vec2(0.0, 0.5));
                return vec4<f32>(p[i], 0.0, 1.0);
            }
            @fragment fn fs() -> @location(0) vec4<f32> { return vec4<f32>(1.0, 0.0, 0.0, 1.0); }
            """u8;
        var wgsl = new WGPUShaderSourceWGSL { Code = WGPUStringView.FromPinned(shader) };
        using WGPUShaderModule module = s_device.CreateShaderModule(new WGPUShaderModuleDescriptor { NextInChain = &wgsl.Chain });

        using WGPUTexture target = s_device.CreateTexture(new WGPUTextureDescriptor
        {
            Size = new WGPUExtent3D { Width = width, Height = height },
            Format = WGPUTextureFormat.RGBA8Unorm,
            Usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.CopySrc,
        });
        using WGPUTextureView view = target.CreateView();

        var colorTarget = new WGPUColorTargetState { Format = WGPUTextureFormat.RGBA8Unorm };
        var fragment = new WGPUFragmentState { Module = module, EntryPoint = WGPUStringView.FromPinned("fs"u8), TargetCount = 1, Targets = &colorTarget };
        using WGPURenderPipeline pipeline = s_device.CreateRenderPipeline(new WGPURenderPipelineDescriptor
        {
            Vertex = new WGPUVertexState { Module = module, EntryPoint = WGPUStringView.FromPinned("vs"u8) },
            Fragment = &fragment,
        });

        const uint bytesPerRow = width * 4; // 256: already aligned to WGPU's 256-byte row pitch
        using WGPUBuffer readback = s_device.CreateBuffer(new WGPUBufferDescriptor { Size = bytesPerRow * height, Usage = WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst });

        using WGPUCommandEncoder encoder = s_device.CreateCommandEncoder();
        var attachment = new WGPURenderPassColorAttachment
        {
            View = view,
            LoadOp = WGPULoadOp.Clear,
            StoreOp = WGPUStoreOp.Store,
            ClearValue = new WGPUColor { R = 0, G = 0, B = 1, A = 1 },
        };
        using (WGPURenderPassEncoder pass = encoder.BeginRenderPass(new WGPURenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &attachment }))
        {
            pass.SetPipeline(pipeline);
            pass.Draw(3, 1, 0, 0);
            pass.End();
        }

        var source = new WGPUTexelCopyTextureInfo { Texture = target };
        var destination = new WGPUTexelCopyBufferInfo { Buffer = readback, Layout = new WGPUTexelCopyBufferLayout { BytesPerRow = bytesPerRow, RowsPerImage = height } };
        var extent = new WGPUExtent3D { Width = width, Height = height };
        encoder.CopyTextureToBuffer(source, destination, extent);
        using WGPUCommandBuffer commands = encoder.Finish();
        s_queue.Submit([commands]);

        readback.MapSync(s_device, WGPUMapMode.Read, 0, bytesPerRow * height);
        ReadOnlySpan<uint> pixels = readback.GetConstMappedRange<uint>(0, (int)(width * height));
        uint center = pixels[(int)(height / 2 * width + width / 2)];
        uint corner = pixels[0];
        readback.Unmap();

        Assert.AreEqual(0xFF0000FFu, center, $"center pixel 0x{center:X8} should be red (RGBA8 little-endian)");
        Assert.AreEqual(0xFFFF0000u, corner, $"corner pixel 0x{corner:X8} should be the blue clear color");
    }
}
