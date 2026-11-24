using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace WGpuFury;

/// <summary>
/// Transient UTF-8 encoding of a <see cref="string"/> into a stack buffer (or native memory when it does not fit),
/// used by the generated <c>string</c> overloads. The memory never moves, so <see cref="View"/> can be passed
/// to native code directly. Dispose it once the call returns.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public unsafe ref struct Utf8Scratch
{
    /// <summary>Size of the stack buffer the generated overloads allocate.</summary>
    public const int StackBufferSize = 256;

    private byte* _heap;
    private readonly WGPUStringView _view;

    /// <summary>Encodes <paramref name="value"/>; <see langword="null"/> becomes the null string view.</summary>
    /// <param name="value">The string to encode.</param>
    /// <param name="stackBuffer">Caller-provided buffer that must not be movable memory (use <c>stackalloc</c>).</param>
    public Utf8Scratch(string? value, Span<byte> stackBuffer)
    {
        if (value == null)
        {
            _view = WGPUStringView.Null;
            return;
        }

        int max = Encoding.UTF8.GetMaxByteCount(value.Length);
        Span<byte> buffer = stackBuffer;
        if (max > stackBuffer.Length)
        {
            _heap = (byte*)NativeMemory.Alloc((nuint)max);
            buffer = new Span<byte>(_heap, max);
        }

        int written = Encoding.UTF8.GetBytes(value, buffer);
        _view = new WGPUStringView((byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(buffer)), (nuint)written);
    }

    /// <summary>The encoded string.</summary>
    public readonly WGPUStringView View => _view;

    /// <summary>Frees the native fallback buffer, if one was needed.</summary>
    public void Dispose()
    {
        if (_heap != null)
        {
            NativeMemory.Free(_heap);
            _heap = null;
        }
    }
}
