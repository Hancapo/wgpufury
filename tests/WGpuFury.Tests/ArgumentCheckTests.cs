namespace WGpuFury.Tests;

/// <summary>
/// wgpu-native aborts the process on a null handle, a missing descriptor or an undefined enum value. The generated
/// methods on handles throw instead, so these mistakes are ordinary .NET exceptions. A missing check would abort
/// the whole test run.
/// </summary>
[TestClass]
public sealed unsafe class ArgumentCheckTests
{
    private static WGPUInstance s_instance;
    private static WGPUAdapter s_adapter;
    private static WGPUDevice s_device;
    private static string? s_unavailable;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        s_instance = WGPU.wgpuCreateInstance(null);
        try
        {
            s_adapter = s_instance.RequestAdapterSync();
            s_device = s_adapter.RequestDeviceSync(s_instance);
        }
        catch (WGPUException e)
        {
            s_unavailable = e.Message;
        }
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        s_device.Dispose();
        s_adapter.Dispose();
        WGPULiveObjects leaked = s_instance.GetLiveObjects();
        s_instance.Dispose();
        Assert.AreEqual(0, leaked.Total, $"the tests leaked wgpu objects: {leaked}");
    }

    private static void RequireDevice()
    {
        if (s_unavailable != null)
        {
            Assert.Inconclusive("No GPU adapter: " + s_unavailable);
        }
    }

    [TestMethod]
    public void MethodsOnANullHandleThrow()
    {
        WGPUDevice nothing = default;
        InvalidOperationException e = Assert.ThrowsExactly<InvalidOperationException>(() => nothing.GetQueue());
        StringAssert.Contains(e.Message, "null WGPUDevice");
        Assert.ThrowsExactly<InvalidOperationException>(() => WGPUBuffer.Null.Release());
        Assert.ThrowsExactly<InvalidOperationException>(() => default(WGPUCommandEncoder).Finish());

        // Dispose keeps doing nothing for the null handle.
        nothing.Dispose();
    }

    [TestMethod]
    public void MissingDescriptorsThrow()
    {
        RequireDevice();
        ArgumentNullException e = Assert.ThrowsExactly<ArgumentNullException>(() => s_device.CreateBuffer((WGPUBufferDescriptor*)null));
        Assert.AreEqual("descriptor", e.ParamName);
        Assert.ThrowsExactly<ArgumentNullException>(() => s_device.CreateTexture((WGPUTextureDescriptor*)null));

        // Nullable descriptors stay optional.
        s_device.CreateCommandEncoder().Dispose();
    }

    [TestMethod]
    public void NullHandleArgumentsThrow()
    {
        RequireDevice();
        using WGPUQueue queue = s_device.GetQueue();
        ArgumentNullException e = Assert.ThrowsExactly<ArgumentNullException>(() => queue.WriteBuffer(default, 0, [1u, 2u]));
        Assert.AreEqual("buffer", e.ParamName);

        // Count + pointer arrays may be null when empty, as in C.
        queue.Submit(0, null);
    }

    [TestMethod]
    public void UndefinedEnumValuesThrow()
    {
        RequireDevice();
        ArgumentOutOfRangeException e = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => s_device.PushErrorScope((WGPUErrorFilter)0x7777));
        Assert.AreEqual("filter", e.ParamName);

        // Defined values work, and feature names stay open-ended.
        s_device.PushErrorScope(WGPUErrorFilter.Validation);
        s_device.PopErrorScope(new WGPUPopErrorScopeCallbackInfo { Mode = WGPUCallbackMode.AllowSpontaneous, Callback = &IgnorePop });
        Assert.IsFalse((bool)s_device.HasFeature((WGPUFeatureName)0x7777));
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void IgnorePop(WGPUPopErrorScopeStatus status, WGPUErrorType type, WGPUStringView message, void* userdata1, void* userdata2)
    {
    }
}
