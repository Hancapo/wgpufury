using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WGpuFury;

public static unsafe partial class WGPU
{
    private static Action<WGPULogLevel, string>? s_logger;

    /// <summary>
    /// Routes wgpu-native's log output to a managed callback (<c>wgpuSetLogCallback</c> + <c>wgpuSetLogLevel</c>).
    /// Pass <see langword="null"/> to stop logging. Exceptions thrown by <paramref name="logger"/> are swallowed,
    /// because they cannot propagate through native frames.
    /// </summary>
    public static void SetLogger(WGPULogLevel level, Action<WGPULogLevel, string>? logger)
    {
        s_logger = logger;
        wgpuSetLogCallback(logger == null ? null : &OnLog, null);
        wgpuSetLogLevel(logger == null ? WGPULogLevel.Off : level);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLog(WGPULogLevel level, WGPUStringView message, void* userdata)
    {
        try
        {
            s_logger?.Invoke(level, message.ToString());
        }
        catch
        {
            // Never let an exception unwind into native code.
        }
    }
}
