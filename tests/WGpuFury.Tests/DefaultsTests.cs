namespace WGpuFury.Tests;

/// <summary>The parameterless constructors apply the header's <c>WGPU_*_INIT</c> defaults.</summary>
[TestClass]
public sealed unsafe class DefaultsTests
{
    [TestMethod]
    public void ColorAttachmentUsesUndefinedDepthSlice()
    {
        var attachment = new WGPURenderPassColorAttachment();
        Assert.AreEqual(WGPU.WGPU_DEPTH_SLICE_UNDEFINED, attachment.DepthSlice);
        Assert.AreEqual(0u, default(WGPURenderPassColorAttachment).DepthSlice, "default() stays zero-filled");
    }

    [TestMethod]
    public void ChainedSourcesCarryTheirSType()
    {
        Assert.AreEqual(WGPUSType.ShaderSourceWGSL, new WGPUShaderSourceWGSL().Chain.SType);
        Assert.AreEqual(WGPUSType.SurfaceSourceWindowsHWND, new WGPUSurfaceSourceWindowsHWND().Chain.SType);
        Assert.AreEqual(WGPUSType.NativeLimits, new WGPUNativeLimits().Chain.SType, "wgpu.h extension STypes are merged into WGPUSType");
    }

    [TestMethod]
    public void LimitsDefaultToUndefined()
    {
        var limits = new WGPULimits();
        Assert.AreEqual(WGPU.WGPU_LIMIT_U32_UNDEFINED, limits.MaxTextureDimension2D);
        Assert.AreEqual(WGPU.WGPU_LIMIT_U64_UNDEFINED, limits.MaxBufferSize);
    }

    [TestMethod]
    public void NestedInitializersApply()
    {
        var descriptor = new WGPURenderPipelineDescriptor();
        Assert.AreEqual(0xFFFFFFFFu, descriptor.Multisample.Mask);
        Assert.AreEqual(1u, descriptor.Multisample.Count);
        Assert.IsTrue(descriptor.Label.IsNull);
        Assert.AreEqual(WGPU.WGPU_WHOLE_SIZE, new WGPUBindGroupEntry().Size);
        Assert.AreEqual(32f, new WGPUSamplerDescriptor().LodMaxClamp);
    }

    [TestMethod]
    public void StringViews()
    {
        WGPUStringView literal = WGPUStringView.FromPinned("main"u8);
        Assert.AreEqual("main", literal.ToString());
        Assert.IsTrue(WGPUStringView.Null.IsNull);
        Assert.IsFalse(WGPUStringView.Empty.IsNull);
        Assert.AreEqual("", WGPUStringView.Null.ToString());

        using var native = new NativeUtf8String("héllo");
        WGPUStringView view = native;
        Assert.AreEqual(6u, (uint)view.Length);
        Assert.AreEqual("héllo", view.ToString());

        byte* terminated = stackalloc byte[] { (byte)'h', (byte)'i', 0 };
        Assert.AreEqual("hi", new WGPUStringView(terminated, WGPU.WGPU_STRLEN).ToString());
    }

    [TestMethod]
    public void Utf8ScratchFallsBackToNativeMemory()
    {
        string big = new('x', 10_000);
        using var scratch = new Utf8Scratch(big, stackalloc byte[Utf8Scratch.StackBufferSize]);
        Assert.AreEqual(big, scratch.View.ToString());

        using var nothing = new Utf8Scratch(null, stackalloc byte[16]);
        Assert.IsTrue(nothing.View.IsNull);
    }

    [TestMethod]
    public void BoolConverts()
    {
        WGPUBool t = true;
        Assert.IsTrue(t);
        Assert.AreEqual(1u, t.Value);
        Assert.IsFalse(default(WGPUBool));
    }
}
