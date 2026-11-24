using System.Diagnostics;

namespace WGpuFury;

/// <summary>
/// <c>WGPUBool</c>: a 32-bit boolean (<c>uint32_t</c>). Converts implicitly to and from <see cref="bool"/>.
/// </summary>
[DebuggerDisplay("{Value != 0}")]
public readonly struct WGPUBool : IEquatable<WGPUBool>
{
    /// <summary>The raw value; anything non-zero is true.</summary>
    public readonly uint Value;

    /// <summary>Creates a value from its raw representation.</summary>
    public WGPUBool(uint value) => Value = value;

    /// <summary><c>WGPU_TRUE</c>.</summary>
    public static WGPUBool True => new(1);

    /// <summary><c>WGPU_FALSE</c>.</summary>
    public static WGPUBool False => default;

    /// <summary>Converts from <see cref="bool"/>.</summary>
    public static implicit operator WGPUBool(bool value) => new(value ? 1u : 0u);

    /// <summary>Converts to <see cref="bool"/>.</summary>
    public static implicit operator bool(WGPUBool value) => value.Value != 0;

    /// <inheritdoc/>
    public bool Equals(WGPUBool other) => (Value != 0) == (other.Value != 0);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is WGPUBool other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => (Value != 0).GetHashCode();

    /// <inheritdoc/>
    public override string ToString() => (Value != 0).ToString();

    /// <summary>Compares truthiness.</summary>
    public static bool operator ==(WGPUBool left, WGPUBool right) => left.Equals(right);

    /// <summary>Compares truthiness.</summary>
    public static bool operator !=(WGPUBool left, WGPUBool right) => !left.Equals(right);
}
