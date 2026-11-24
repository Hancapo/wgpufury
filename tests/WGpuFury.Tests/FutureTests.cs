using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WGpuFury.Tests;

/// <summary>
/// The functions wgpu-native's wgpu 30 port still left unimplemented, which eng/patches implements:
/// futures and callback modes, WaitAny, async pipeline creation, the device-lost future, mapped-range copies,
/// instance features, GetProcAddress, error-scope edge cases and the texture binding view dimension.
/// Tests that need a device are inconclusive (not failed) on machines without any adapter.
/// </summary>
[TestClass]
public sealed unsafe class FutureTests
{
    private const ulong Forever = ulong.MaxValue;

    private static WGPUInstance s_instance;
    private static WGPUAdapter s_adapter;
    private static WGPUDevice s_device;
    private static WGPUQueue s_queue;
    private static string? s_unavailable;
    private static int s_uncapturedErrors;

    /// <summary>What a callback reported; tests point userdata1 at a stack instance.</summary>
    private struct Event
    {
        public int Calls;
        public int Status;
        public int Kind;
        public nint Handle;
        public string? Message;
    }

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        WGPUInstanceFeatureName timedWaitAny = WGPUInstanceFeatureName.TimedWaitAny;
        var limits = new WGPUInstanceLimits { TimedWaitAnyMaxCount = 8 };
        s_instance = WGPU.wgpuCreateInstance(new WGPUInstanceDescriptor
        {
            RequiredFeatureCount = 1,
            RequiredFeatures = &timedWaitAny,
            RequiredLimits = &limits,
        });
        try
        {
            s_adapter = s_instance.RequestAdapterSync();
        }
        catch (WGPUException e)
        {
            s_unavailable = e.Message;
            return;
        }

        s_device = s_adapter.RequestDeviceSync(s_instance, new WGPUDeviceDescriptor
        {
            UncapturedErrorCallbackInfo = new WGPUUncapturedErrorCallbackInfo { Callback = &OnUncapturedError },
        });
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
    public void Reset() => s_uncapturedErrors = 0;

    [TestCleanup]
    public void NoUncapturedErrors() => Assert.AreEqual(0, s_uncapturedErrors, "uncaptured WebGPU errors were reported");

    private static void RequireDevice()
    {
        if (s_unavailable != null)
        {
            Assert.Inconclusive("No GPU adapter: " + s_unavailable);
        }
    }

    // ---------------------------------------------------------------- no device needed

    [TestMethod]
    public void InstanceFeaturesAndLimitsAreReported()
    {
        var features = new WGPUSupportedInstanceFeatures();
        WGPU.wgpuGetInstanceFeatures(&features);
        var list = new ReadOnlySpan<WGPUInstanceFeatureName>(features.Features, (int)features.FeatureCount).ToArray();
        features.FreeMembers();

        CollectionAssert.Contains(list, WGPUInstanceFeatureName.TimedWaitAny);
        CollectionAssert.Contains(list, WGPUInstanceFeatureName.ShaderSourceSPIRV);
        CollectionAssert.Contains(list, WGPUInstanceFeatureName.MultipleDevicesPerAdapter);
        Assert.IsTrue((bool)WGPU.wgpuHasInstanceFeature(WGPUInstanceFeatureName.TimedWaitAny));
        Assert.IsFalse((bool)WGPU.wgpuHasInstanceFeature((WGPUInstanceFeatureName)0x7777));

        var limits = new WGPUInstanceLimits();
        Assert.AreEqual(WGPUStatus.Success, WGPU.wgpuGetInstanceLimits(&limits));
        Assert.AreEqual((nuint)64, limits.TimedWaitAnyMaxCount);
    }

    [TestMethod]
    public void CreateInstanceRejectsUnsupportedRequirements()
    {
        var tooMany = new WGPUInstanceLimits { TimedWaitAnyMaxCount = 65 };
        Assert.IsTrue(WGPU.wgpuCreateInstance(new WGPUInstanceDescriptor { RequiredLimits = &tooMany }).IsNull);

        var unknown = (WGPUInstanceFeatureName)0x7777;
        Assert.IsTrue(WGPU.wgpuCreateInstance(new WGPUInstanceDescriptor { RequiredFeatureCount = 1, RequiredFeatures = &unknown }).IsNull);
    }

    [TestMethod]
    public void GetProcAddressResolvesEveryExport()
    {
        foreach (string name in NativeExports.Functions)
        {
            Assert.IsTrue(WGPU.wgpuGetProcAddress(name) != null, name);
        }

        Assert.IsTrue(WGPU.wgpuGetProcAddress("wgpuNoSuchFunction") == null);
        var getVersion = (delegate* unmanaged[Cdecl]<uint>)WGPU.wgpuGetProcAddress("wgpuGetVersion"u8);
        Assert.AreEqual(WGPU.wgpuGetVersion(), getVersion());
    }

    [TestMethod]
    public void TimedWaitRequiresTheInstanceFeature()
    {
        using WGPUInstance plain = WGPU.wgpuCreateInstance(null);
        var ev = new Event();
        WGPUFuture future = plain.RequestAdapter(null, new WGPURequestAdapterCallbackInfo
        {
            Mode = WGPUCallbackMode.WaitAnyOnly,
            Callback = &OnAdapter,
            Userdata1 = Unsafe.AsPointer(ref ev),
        });
        Assert.AreNotEqual(0ul, future.Id);
        Assert.AreEqual(0, ev.Calls, "a WaitAnyOnly callback must not fire before WaitAny");

        Span<WGPUFutureWaitInfo> wait = [new WGPUFutureWaitInfo { Future = future }];
        Assert.AreEqual(WGPUWaitStatus.Error, plain.WaitAny(wait, 1_000_000));
        Assert.AreEqual(0, ev.Calls);

        Assert.AreEqual(WGPUWaitStatus.Success, plain.WaitAny(wait, 0));
        Assert.IsTrue((bool)wait[0].Completed);
        Assert.AreEqual(1, ev.Calls);
        new WGPUAdapter(ev.Handle).Dispose();

        // Waiting again on a future whose callback already fired reports it completed, without firing again.
        Assert.AreEqual(WGPUWaitStatus.Success, plain.WaitAny(wait, 0));
        Assert.AreEqual(1, ev.Calls);
    }

    [TestMethod]
    public void WaitAnyRejectsInvalidFutures()
    {
        Span<WGPUFutureWaitInfo> wait = [new WGPUFutureWaitInfo { Future = new WGPUFuture { Id = ulong.MaxValue } }];
        Assert.AreEqual(WGPUWaitStatus.Error, s_instance.WaitAny(wait, 0));
        Assert.AreEqual(WGPUWaitStatus.Success, s_instance.WaitAny(0, null, 0));
    }

    [TestMethod]
    public void ReleasingTheInstanceCancelsPendingCallbacks()
    {
        var ev = new Event();
        WGPUInstance instance = WGPU.wgpuCreateInstance(null);
        instance.RequestAdapter(null, new WGPURequestAdapterCallbackInfo
        {
            Mode = WGPUCallbackMode.WaitAnyOnly,
            Callback = &OnAdapter,
            Userdata1 = Unsafe.AsPointer(ref ev),
        });
        Assert.AreEqual(0, ev.Calls);
        instance.Dispose();
        Assert.AreEqual(1, ev.Calls);
        Assert.AreEqual((int)WGPURequestAdapterStatus.CallbackCancelled, ev.Status);
        Assert.AreEqual(0, ev.Handle);
    }

    // ---------------------------------------------------------------- callback modes

    [TestMethod]
    public void WaitAnyOnlyCallbacksFireOnlyInsideWaitAny()
    {
        RequireDevice();
        using WGPUBuffer buffer = s_device.CreateBuffer(new WGPUBufferDescriptor { Size = 256, Usage = WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst });
        var ev = new Event();
        WGPUFuture future = buffer.MapAsync(WGPUMapMode.Read, 0, 256, new WGPUBufferMapCallbackInfo
        {
            Mode = WGPUCallbackMode.WaitAnyOnly,
            Callback = &OnMapped,
            Userdata1 = Unsafe.AsPointer(ref ev),
        });

        WGPU.wgpuDevicePoll(s_device, true, null, 0);
        s_instance.ProcessEvents();
        Assert.AreEqual(0, ev.Calls, "neither polling nor ProcessEvents may fire a WaitAnyOnly callback");

        Span<WGPUFutureWaitInfo> wait = [new WGPUFutureWaitInfo { Future = future }];
        Assert.AreEqual(WGPUWaitStatus.Success, s_instance.WaitAny(wait, 0));
        Assert.IsTrue((bool)wait[0].Completed);
        Assert.AreEqual(1, ev.Calls);
        Assert.AreEqual((int)WGPUMapAsyncStatus.Success, ev.Status);
        buffer.Unmap();
    }

    [TestMethod]
    public void ProcessEventsFiresAllowProcessEventsCallbacks()
    {
        RequireDevice();
        var ev = new Event();
        s_queue.Submit([]);
        s_queue.OnSubmittedWorkDone(new WGPUQueueWorkDoneCallbackInfo
        {
            Mode = WGPUCallbackMode.AllowProcessEvents,
            Callback = &OnWorkDone,
            Userdata1 = Unsafe.AsPointer(ref ev),
        });

        WGPU.wgpuDevicePoll(s_device, true, null, 0);
        Assert.AreEqual(0, ev.Calls, "polling alone must not fire an AllowProcessEvents callback");
        s_instance.ProcessEvents();
        Assert.AreEqual(1, ev.Calls);
        Assert.AreEqual((int)WGPUQueueWorkDoneStatus.Success, ev.Status);
    }

    [TestMethod]
    public void TimedWaitAnyBlocksUntilTheGpuIsDone()
    {
        RequireDevice();
        const int count = 1 << 16;
        ReadOnlySpan<byte> shader = """
            @group(0) @binding(0) var<storage, read_write> data: array<u32>;
            @compute @workgroup_size(64)
            fn main(@builtin(global_invocation_id) id: vec3<u32>) { data[id.x] = id.x * 2u; }
            """u8;
        var wgsl = new WGPUShaderSourceWGSL { Code = WGPUStringView.FromPinned(shader) };
        using WGPUShaderModule module = s_device.CreateShaderModule(new WGPUShaderModuleDescriptor { NextInChain = &wgsl.Chain });
        using WGPUComputePipeline pipeline = s_device.CreateComputePipeline(new WGPUComputePipelineDescriptor
        {
            Compute = new WGPUComputeState { Module = module, EntryPoint = WGPUStringView.FromPinned("main"u8) },
        });
        const ulong size = count * sizeof(uint);
        using WGPUBuffer storage = s_device.CreateBuffer(new WGPUBufferDescriptor { Size = size, Usage = WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc });
        using WGPUBuffer readback = s_device.CreateBuffer(new WGPUBufferDescriptor { Size = size, Usage = WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst });
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

        var ev = new Event();
        WGPUFuture future = readback.MapAsync(WGPUMapMode.Read, 0, (nuint)size, new WGPUBufferMapCallbackInfo
        {
            Mode = WGPUCallbackMode.WaitAnyOnly,
            Callback = &OnMapped,
            Userdata1 = Unsafe.AsPointer(ref ev),
        });
        Span<WGPUFutureWaitInfo> wait = [new WGPUFutureWaitInfo { Future = future }];
        Assert.AreEqual(WGPUWaitStatus.Success, s_instance.WaitAny(wait, Forever));
        Assert.AreEqual(1, ev.Calls);
        Assert.AreEqual((int)WGPUMapAsyncStatus.Success, ev.Status);

        ReadOnlySpan<uint> data = readback.GetConstMappedRange<uint>(0, count);
        Assert.AreEqual(2u * (count - 1), data[count - 1]);
        readback.Unmap();
    }

    [TestMethod]
    public void WaitAnyTimesOutOnFuturesThatDoNotComplete()
    {
        RequireDevice();
        Span<WGPUFutureWaitInfo> wait = [new WGPUFutureWaitInfo { Future = s_device.GetLostFuture() }];
        Assert.AreNotEqual(0ul, wait[0].Future.Id);
        Assert.AreEqual(WGPUWaitStatus.TimedOut, s_instance.WaitAny(wait, 0));
        Assert.AreEqual(WGPUWaitStatus.TimedOut, s_instance.WaitAny(wait, 2_000_000));
        Assert.IsFalse((bool)wait[0].Completed);

        Span<WGPUFutureWaitInfo> tooMany = new WGPUFutureWaitInfo[9];
        tooMany.Fill(wait[0]);
        Assert.AreEqual(WGPUWaitStatus.Error, s_instance.WaitAny(tooMany, 1), "more futures than the requested timedWaitAnyMaxCount");
    }

    // ---------------------------------------------------------------- device lost

    [TestMethod]
    public void DeviceLostFutureCompletesWhenTheDeviceIsDestroyed()
    {
        RequireDevice();
        var ev = new Event();
        using WGPUDevice device = s_adapter.RequestDeviceSync(s_instance, new WGPUDeviceDescriptor
        {
            DeviceLostCallbackInfo = new WGPUDeviceLostCallbackInfo
            {
                Mode = WGPUCallbackMode.AllowProcessEvents,
                Callback = &OnDeviceLost,
                Userdata1 = Unsafe.AsPointer(ref ev),
            },
        });
        WGPUFuture lost = device.GetLostFuture();
        Span<WGPUFutureWaitInfo> wait = [new WGPUFutureWaitInfo { Future = lost }];
        Assert.AreEqual(WGPUWaitStatus.TimedOut, s_instance.WaitAny(wait, 0));

        device.Destroy();
        Assert.AreEqual(0, ev.Calls, "an AllowProcessEvents callback waits for ProcessEvents or WaitAny");
        Assert.AreEqual(WGPUWaitStatus.Success, s_instance.WaitAny(wait, 0));
        Assert.AreEqual(1, ev.Calls);
        Assert.AreEqual((int)WGPUDeviceLostReason.Destroyed, ev.Status);
        Assert.AreEqual(device.Handle, ev.Handle);
    }

    [TestMethod]
    public void DeviceLostCallbackFiresOnRelease()
    {
        RequireDevice();
        var ev = new Event();
        WGPUDevice device = s_adapter.RequestDeviceSync(s_instance, new WGPUDeviceDescriptor
        {
            DeviceLostCallbackInfo = new WGPUDeviceLostCallbackInfo
            {
                Mode = WGPUCallbackMode.AllowSpontaneous,
                Callback = &OnDeviceLost,
                Userdata1 = Unsafe.AsPointer(ref ev),
            },
        });
        device.Dispose();
        Assert.AreEqual(1, ev.Calls);
        Assert.AreEqual((int)WGPUDeviceLostReason.Destroyed, ev.Status);
    }

    // ---------------------------------------------------------------- async pipelines

    [TestMethod]
    public void CreateComputePipelineAsyncSucceeds()
    {
        RequireDevice();
        using WGPUShaderModule module = Module("@compute @workgroup_size(1) fn main() {}"u8);
        var ev = new Event();
        WGPUFuture future = s_device.CreateComputePipelineAsync(
            new WGPUComputePipelineDescriptor { Compute = new WGPUComputeState { Module = module, EntryPoint = WGPUStringView.FromPinned("main"u8) } },
            new WGPUCreateComputePipelineAsyncCallbackInfo { Mode = WGPUCallbackMode.WaitAnyOnly, Callback = &OnComputePipeline, Userdata1 = Unsafe.AsPointer(ref ev) });

        Span<WGPUFutureWaitInfo> wait = [new WGPUFutureWaitInfo { Future = future }];
        Assert.AreEqual(WGPUWaitStatus.Success, s_instance.WaitAny(wait, 0));
        Assert.AreEqual(1, ev.Calls);
        Assert.AreEqual((int)WGPUCreatePipelineAsyncStatus.Success, ev.Status, ev.Message);
        using var pipeline = new WGPUComputePipeline(ev.Handle);
        Assert.IsFalse(pipeline.IsNull);
        pipeline.GetBindGroupLayout(0).Dispose();
    }

    [TestMethod]
    public void CreateRenderPipelineAsyncReportsValidationErrorsToTheCallback()
    {
        RequireDevice();
        using WGPUShaderModule module = Module("@vertex fn vs() -> @builtin(position) vec4f { return vec4f(); }"u8);
        var ev = new Event();
        s_device.PushErrorScope(WGPUErrorFilter.Validation);
        s_device.CreateRenderPipelineAsync(
            new WGPURenderPipelineDescriptor { Vertex = new WGPUVertexState { Module = module, EntryPoint = WGPUStringView.FromPinned("missing"u8) } },
            new WGPUCreateRenderPipelineAsyncCallbackInfo { Mode = WGPUCallbackMode.AllowSpontaneous, Callback = &OnRenderPipeline, Userdata1 = Unsafe.AsPointer(ref ev) });

        Assert.AreEqual(1, ev.Calls);
        Assert.AreEqual((int)WGPUCreatePipelineAsyncStatus.ValidationError, ev.Status);
        Assert.AreEqual(0, ev.Handle);
        Assert.IsFalse(string.IsNullOrEmpty(ev.Message));

        // The error went to the callback, not to error scopes or the uncaptured-error callback.
        Event scope = PopErrorScope();
        Assert.AreEqual((int)WGPUErrorType.NoError, scope.Kind);
    }

    // ---------------------------------------------------------------- error scopes

    [TestMethod]
    public void PoppingAnEmptyErrorScopeStackReportsAnError()
    {
        RequireDevice();
        Event ev = PopErrorScope();
        Assert.AreEqual((int)WGPUPopErrorScopeStatus.Error, ev.Status);
        Assert.AreEqual((int)WGPUErrorType.NoError, ev.Kind);
        Assert.IsFalse(string.IsNullOrEmpty(ev.Message));
    }

    [TestMethod]
    public void ErrorScopesCatchValidationAndAcceptTheInternalFilter()
    {
        RequireDevice();
        s_device.PushErrorScope(WGPUErrorFilter.Internal);
        Assert.AreEqual((int)WGPUErrorType.NoError, PopErrorScope().Kind);

        s_device.PushErrorScope(WGPUErrorFilter.Validation);
        s_device.CreateBuffer(new WGPUBufferDescriptor { Size = 6, Usage = WGPUBufferUsage.CopyDst, MappedAtCreation = true }).Dispose();
        Event ev = PopErrorScope();
        Assert.AreEqual((int)WGPUPopErrorScopeStatus.Success, ev.Status);
        Assert.AreEqual((int)WGPUErrorType.Validation, ev.Kind);
        Assert.IsFalse(string.IsNullOrEmpty(ev.Message));
    }

    // ---------------------------------------------------------------- buffers and textures

    [TestMethod]
    public void ReadAndWriteMappedRangeCopyData()
    {
        RequireDevice();
        uint[] values = [1, 2, 3, 4, 5, 6, 7, 8];
        using WGPUBuffer upload = s_device.CreateBuffer(new WGPUBufferDescriptor { Size = 32, Usage = WGPUBufferUsage.CopySrc, MappedAtCreation = true });
        Assert.AreEqual(WGPUStatus.Success, upload.WriteMappedRange<uint>(0, values));
        Assert.AreEqual(WGPUStatus.Error, upload.WriteMappedRange<uint>(16, values), "out of range");
        upload.Unmap();
        Assert.AreEqual(WGPUStatus.Error, upload.WriteMappedRange<uint>(0, values), "not mapped");
        Assert.IsTrue(WGPU.wgpuBufferGetMappedRange(upload, 0, 32) == null, "GetMappedRange on an unmapped buffer returns NULL");

        using WGPUBuffer readback = s_device.CreateBuffer(new WGPUBufferDescriptor { Size = 32, Usage = WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst });
        using WGPUCommandEncoder encoder = s_device.CreateCommandEncoder();
        encoder.CopyBufferToBuffer(upload, 0, readback, 0, 32);
        using WGPUCommandBuffer commands = encoder.Finish();
        s_queue.Submit([commands]);
        readback.MapSync(s_device, WGPUMapMode.Read, 0, 32);

        uint[] copy = new uint[4];
        Assert.AreEqual(WGPUStatus.Success, readback.ReadMappedRange<uint>(16, copy));
        CollectionAssert.AreEqual(values[4..], copy);
        readback.Unmap();
    }

    [TestMethod]
    public void TextureBindingViewDimensionComesFromTheDescriptorChain()
    {
        RequireDevice();
        var descriptor = new WGPUTextureDescriptor
        {
            Size = new WGPUExtent3D { Width = 4, Height = 4, DepthOrArrayLayers = 2 },
            Format = WGPUTextureFormat.RGBA8Unorm,
            Usage = WGPUTextureUsage.TextureBinding,
        };
        using (WGPUTexture plain = s_device.CreateTexture(descriptor))
        {
            Assert.AreEqual(WGPUTextureViewDimension.Undefined, plain.GetTextureBindingViewDimension());
        }

        var binding = new WGPUTextureBindingViewDimension { TextureBindingViewDimension = WGPUTextureViewDimension._2DArray };
        descriptor.NextInChain = &binding.Chain;
        using WGPUTexture chained = s_device.CreateTexture(descriptor);
        Assert.AreEqual(WGPUTextureViewDimension._2DArray, chained.GetTextureBindingViewDimension());
    }

    // ---------------------------------------------------------------- helpers

    private static WGPUShaderModule Module(ReadOnlySpan<byte> wgslSource)
    {
        fixed (byte* p = wgslSource)
        {
            var wgsl = new WGPUShaderSourceWGSL { Code = new WGPUStringView(p, (nuint)wgslSource.Length) };
            return s_device.CreateShaderModule(new WGPUShaderModuleDescriptor { NextInChain = &wgsl.Chain });
        }
    }

    private static Event PopErrorScope()
    {
        var ev = new Event();
        s_device.PopErrorScope(new WGPUPopErrorScopeCallbackInfo
        {
            Mode = WGPUCallbackMode.AllowSpontaneous,
            Callback = &OnPopErrorScope,
            Userdata1 = Unsafe.AsPointer(ref ev),
        });
        Assert.AreEqual(1, ev.Calls);
        return ev;
    }

    private static ref Event Record(void* userdata, int status)
    {
        ref Event ev = ref Unsafe.AsRef<Event>(userdata);
        ev.Calls++;
        ev.Status = status;
        return ref ev;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUncapturedError(WGPUDevice* device, WGPUErrorType type, WGPUStringView message, void* userdata1, void* userdata2)
    {
        Interlocked.Increment(ref s_uncapturedErrors);
        Console.Error.WriteLine($"{type}: {message}");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnAdapter(WGPURequestAdapterStatus status, WGPUAdapter adapter, WGPUStringView message, void* userdata1, void* userdata2)
    {
        ref Event ev = ref Record(userdata1, (int)status);
        ev.Handle = adapter.Handle;
        ev.Message = message.ToString();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMapped(WGPUMapAsyncStatus status, WGPUStringView message, void* userdata1, void* userdata2) =>
        Record(userdata1, (int)status).Message = message.ToString();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWorkDone(WGPUQueueWorkDoneStatus status, WGPUStringView message, void* userdata1, void* userdata2) =>
        Record(userdata1, (int)status).Message = message.ToString();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDeviceLost(WGPUDevice* device, WGPUDeviceLostReason reason, WGPUStringView message, void* userdata1, void* userdata2)
    {
        ref Event ev = ref Record(userdata1, (int)reason);
        ev.Handle = device->Handle;
        ev.Message = message.ToString();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnComputePipeline(WGPUCreatePipelineAsyncStatus status, WGPUComputePipeline pipeline, WGPUStringView message, void* userdata1, void* userdata2)
    {
        ref Event ev = ref Record(userdata1, (int)status);
        ev.Handle = pipeline.Handle;
        ev.Message = message.ToString();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRenderPipeline(WGPUCreatePipelineAsyncStatus status, WGPURenderPipeline pipeline, WGPUStringView message, void* userdata1, void* userdata2)
    {
        ref Event ev = ref Record(userdata1, (int)status);
        ev.Handle = pipeline.Handle;
        ev.Message = message.ToString();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPopErrorScope(WGPUPopErrorScopeStatus status, WGPUErrorType type, WGPUStringView message, void* userdata1, void* userdata2)
    {
        ref Event ev = ref Record(userdata1, (int)status);
        ev.Kind = (int)type;
        ev.Message = message.ToString();
    }
}
