namespace WGpuFury;

public static partial class WGPU
{
    /// <summary>
    /// The native library name used by every import: <c>wgpu_native.dll</c>, <c>libwgpu_native.so</c> or
    /// <c>libwgpu_native.dylib</c>. Under Native AOT it can be linked statically via <c>DirectPInvoke</c>.
    /// </summary>
    public const string LibraryName = "wgpu_native";

    /// <summary>
    /// The version reported by the loaded native library, decoded from <see cref="wgpuGetVersion"/>
    /// (one byte per component, most significant first).
    /// </summary>
    public static Version NativeVersion
    {
        get
        {
            uint v = wgpuGetVersion();
            return new Version((int)(v >> 24), (int)((v >> 16) & 0xFF), (int)((v >> 8) & 0xFF), (int)(v & 0xFF));
        }
    }
}
