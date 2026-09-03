using System;
using RedBird.Core.Memory;

namespace RedBird.Core.Platform;


/// <summary>One mapped range as reported by <c>/proc/self/maps</c>.</summary>
public readonly struct MappedRegion(UInt64 start, UInt64 end, MemoryProtection protection, bool isPrivate, string? path)
{
    /// <summary>First address in the range.</summary>
    public UInt64 Start { get; } = start;

    /// <summary>One past the last address in the range.</summary>
    public UInt64 End { get; } = end;

    /// <summary>Current page protection.</summary>
    public MemoryProtection Protection { get; } = protection;

    /// <summary>True for a private (copy-on-write) mapping, false for shared.</summary>
    public bool IsPrivate { get; } = isPrivate;

    /// <summary>Backing file, or null for an anonymous mapping.</summary>
    public string? Path { get; } = path;

    /// <summary>Length of the range in bytes.</summary>
    public UInt64 Length => End - Start;

    /// <inheritdoc/>
    public override string ToString() => $"0x{Start:X16}-0x{End:X16} {Protection} {Path ?? "[anon]"}";
}
