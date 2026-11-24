using System.Runtime.InteropServices;
using System.Text;

namespace WGpuFury;

/// <summary>
/// A null-terminated UTF-8 string in native memory, for <see cref="WGPUStringView"/> fields of descriptors
/// built from runtime strings (labels, entry points, shader code). Literals can use
/// <see cref="WGPUStringView.FromPinned"/> with <c>"..."u8</c> instead.
/// <code>
/// using var code = new NativeUtf8String(wgsl);
/// var source = new WGPUShaderSourceWGSL { Code = code };
/// </code>
/// </summary>
public sealed unsafe class NativeUtf8String : IDisposable
{
    private byte* _data;
    private readonly int _length;

    /// <summary>Copies <paramref name="value"/> into native memory as UTF-8.</summary>
    public NativeUtf8String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _length = Encoding.UTF8.GetByteCount(value);
        _data = (byte*)NativeMemory.Alloc((nuint)_length + 1);
        Encoding.UTF8.GetBytes(value, new Span<byte>(_data, _length));
        _data[_length] = 0;
    }

    /// <summary>The string as a view with an explicit length. Valid until this instance is disposed.</summary>
    public WGPUStringView View
    {
        get
        {
            ObjectDisposedException.ThrowIf(_data == null, this);
            return new WGPUStringView(_data, (nuint)_length);
        }
    }

    /// <summary>Converts to a view. The view is only valid until this instance is disposed.</summary>
    public static implicit operator WGPUStringView(NativeUtf8String value) => value.View;

    /// <summary>Frees the native memory.</summary>
    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    /// <summary>Frees the native memory if <see cref="Dispose"/> was not called.</summary>
    ~NativeUtf8String() => Free();

    private void Free()
    {
        if (_data != null)
        {
            NativeMemory.Free(_data);
            _data = null;
        }
    }
}
