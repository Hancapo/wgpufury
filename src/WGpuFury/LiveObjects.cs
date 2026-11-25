namespace WGpuFury;

/// <summary>
/// The wgpu objects of an instance that are still alive: created and not yet released. Handles are value types
/// with no finalizer, so a missed <c>Dispose</c> leaks silently; this is wgpu's own count of what is left
/// (<c>wgpuGenerateReport</c>), for a leak check in tests or debug builds.
/// </summary>
/// <param name="Counts">The object types with live objects, and how many of each, e.g. ("buffers", 2).</param>
public sealed record WGPULiveObjects(IReadOnlyList<(string Type, int Count)> Counts)
{
    /// <summary>The number of live objects of all types.</summary>
    public int Total => Counts.Sum(c => c.Count);

    /// <summary>For example "2 buffers, 1 texture views", or "none".</summary>
    public override string ToString() =>
        Counts.Count == 0 ? "none" : string.Join(", ", Counts.Select(c => $"{c.Count} {c.Type}"));
}

public unsafe partial struct WGPUInstance
{
    /// <summary>
    /// Counts the objects created from this instance that haven't been released yet (<c>wgpuGenerateReport</c>).
    /// The instance itself isn't counted. Release everything else first, then expect <see cref="WGPULiveObjects.Total"/>
    /// to be 0:
    /// <code>
    /// queue.Dispose(); device.Dispose(); adapter.Dispose();
    /// Debug.Assert(instance.GetLiveObjects().Total == 0, $"leaked: {instance.GetLiveObjects()}");
    /// </code>
    /// </summary>
    public WGPULiveObjects GetLiveObjects()
    {
        WGPUGlobalReport report;
        WGPU.wgpuGenerateReport(this, &report);
        ref WGPUHubReport hub = ref report.Hub;
        (string, WGPURegistryReport)[] registries =
        [
            ("surfaces", report.Surfaces),
            ("adapters", hub.Adapters),
            ("devices", hub.Devices),
            ("queues", hub.Queues),
            ("pipeline layouts", hub.PipelineLayouts),
            ("shader modules", hub.ShaderModules),
            ("bind group layouts", hub.BindGroupLayouts),
            ("bind groups", hub.BindGroups),
            ("command buffers", hub.CommandBuffers),
            ("render bundles", hub.RenderBundles),
            ("render pipelines", hub.RenderPipelines),
            ("compute pipelines", hub.ComputePipelines),
            ("pipeline caches", hub.PipelineCaches),
            ("query sets", hub.QuerySets),
            ("buffers", hub.Buffers),
            ("textures", hub.Textures),
            ("texture views", hub.TextureViews),
            ("samplers", hub.Samplers),
        ];
        return new WGPULiveObjects(registries
            .Where(r => r.Item2.NumKeptFromUser != 0)
            .Select(r => (r.Item1, (int)r.Item2.NumKeptFromUser))
            .ToArray());
    }
}
