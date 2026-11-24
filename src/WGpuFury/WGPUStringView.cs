using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace WGpuFury;

public unsafe partial struct WGPUStringView
{
    /// <summary>Creates a view over <paramref name="length"/> UTF-8 bytes at <paramref name="data"/>.</summary>
    public WGPUStringView(byte* data, nuint length)
    {
        Data = data;
        Length = length;
    }

    /// <summary>The null string view, <c>{NULL, WGPU_STRLEN}</c> (same as <c>WGPU_STRING_VIEW_INIT</c>).</summary>
    public static WGPUStringView Null => new(null, nuint.MaxValue);

    /// <summary>The empty string view, <c>{NULL, 0}</c>.</summary>
    public static WGPUStringView Empty => default;

    /// <summary>Whether this is the null value (as opposed to the empty string).</summary>
    public readonly bool IsNull => Data == null && Length == nuint.MaxValue;

    /// <summary>
    /// Creates a view over memory that will not move: a UTF-8 literal (<c>"main"u8</c>), a <c>stackalloc</c>
    /// buffer, native memory, or a pinned array. Never pass a span over a regular managed array:
    /// the GC may move it while the native side still holds the pointer.
    /// </summary>
    public static WGPUStringView FromPinned(ReadOnlySpan<byte> utf8) =>
        new((byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(utf8)), (nuint)utf8.Length);

    /// <summary>The bytes of the string; resolves <c>WGPU_STRLEN</c> (null-terminated) views.</summary>
    public readonly ReadOnlySpan<byte> AsSpan()
    {
        if (Data == null)
        {
            return default;
        }

        return Length == nuint.MaxValue
            ? MemoryMarshal.CreateReadOnlySpanFromNullTerminated(Data)
            : new ReadOnlySpan<byte>(Data, checked((int)Length));
    }

    /// <summary>Decodes the string; returns <c>""</c> for both null and empty views.</summary>
    public override readonly string ToString() => Encoding.UTF8.GetString(AsSpan());
}
