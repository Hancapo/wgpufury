namespace WGpuFury.Tests;

/// <summary><see cref="WGPUInstance.GetLiveObjects"/>: wgpu's count of the objects that haven't been released.</summary>
[TestClass]
public sealed unsafe class LiveObjectsTests
{
    [TestMethod]
    public void UnreleasedObjectsAreCountedUntilReleased()
    {
        using WGPUInstance instance = WGPU.wgpuCreateInstance(null);
        Assert.AreEqual(0, instance.GetLiveObjects().Total, instance.GetLiveObjects().ToString());

        WGPUAdapter adapter;
        try
        {
            adapter = instance.RequestAdapterSync();
        }
        catch (WGPUException e)
        {
            Assert.Inconclusive("No GPU adapter: " + e.Message);
            return;
        }

        WGPUDevice device = adapter.RequestDeviceSync(instance);
        WGPUQueue queue = device.GetQueue();
        WGPUBuffer first = device.CreateBuffer(new WGPUBufferDescriptor { Size = 256, Usage = WGPUBufferUsage.CopyDst });
        WGPUBuffer second = device.CreateBuffer(new WGPUBufferDescriptor { Size = 256, Usage = WGPUBufferUsage.CopyDst });

        WGPULiveObjects live = instance.GetLiveObjects();
        CollectionAssert.Contains(live.Counts.ToArray(), ("buffers", 2), live.ToString());
        CollectionAssert.Contains(live.Counts.ToArray(), ("devices", 1), live.ToString());
        StringAssert.Contains(live.ToString(), "2 buffers");

        first.Dispose();
        CollectionAssert.Contains(instance.GetLiveObjects().Counts.ToArray(), ("buffers", 1));
        second.Dispose();
        Assert.IsFalse(instance.GetLiveObjects().Counts.Any(c => c.Type == "buffers"), instance.GetLiveObjects().ToString());

        queue.Dispose();
        device.Dispose();
        adapter.Dispose();
        Assert.AreEqual(0, instance.GetLiveObjects().Total, instance.GetLiveObjects().ToString());
        Assert.AreEqual("none", instance.GetLiveObjects().ToString());
    }
}
