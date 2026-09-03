using System;

namespace RedBird.Core.Platform;

/// <summary>A contiguous executable range within a loaded module image.</summary>
public readonly unsafe struct ModuleImageRegion(IntPtr address, int length, string name)
{
    /// <summary>Address of the first byte.</summary>
    public IntPtr Address { get; } = address;

    /// <summary>Length in bytes.</summary>
    public int Length { get; } = length;

    /// <summary>Container-provided section or segment name.</summary>
    public string Name { get; } = name;

    /// <summary>
    /// Direct view over the loaded image. The module must remain loaded while the span is used.
    /// </summary>
    public ReadOnlySpan<byte> Span => new(Address.ToPointer(), Length);

    /// <summary>Converts a region-relative offset into an absolute address.</summary>
    public UInt64 ToAddress(long offset) => unchecked((UInt64)Address.ToInt64() + (UInt64)offset);

    /// <inheritdoc/>
    public override string ToString() => $"{Name} (0x{Address.ToInt64():X16}, {Length} bytes)";
}
