using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace WGpuFury;

/// <summary>
/// Throw helpers for the argument checks in the generated methods on handles. wgpu-native aborts the process on a
/// null handle, a missing descriptor or an undefined enum value, so those methods throw first. The raw
/// <c>WGPU.wgpu*</c> functions don't check anything, as in C.
/// </summary>
internal static class Check
{
    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowNullHandle(string type) =>
        throw new InvalidOperationException($"Called a method on a null {type} (a default value, or a failed creation).");

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowNullArgument(string name) =>
        throw new ArgumentNullException(name);

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowUndefined<T>(string name, T value)
        where T : struct, Enum =>
        throw new ArgumentOutOfRangeException(name, value, $"Not a defined {typeof(T).Name} value.");
}
