using System;

namespace RedBird.Abstractions.Hooks;

/// <summary>
/// A location to hook.
/// Implicit conversions keep existing call sites source-compatible:
/// <code>
/// tx.AddDetour(handle, "48 89 5C 24 ?? 57", callback);   // string  -> Pattern
/// tx.AddDetour(handle, 0x7FF712345678UL,    callback);   // ulong   -> Address
/// tx.AddDetour(handle, HookTarget.Export("DLL_ImportAIV"), callback);
/// </code>
/// </summary>
public readonly struct HookTarget : IEquatable<HookTarget>
{
    private HookTarget(HookTargetKind kind, string? text, UInt64 value, int offset, IntPtr module)
    {
        Kind = kind;
        Text = text;
        Value = value;
        Offset = offset;
        Module = module;
    }

    /// <summary>Discriminator for the union below.</summary>
    public HookTargetKind Kind { get; }

    /// <summary>Pattern string or export name, depending on <see cref="Kind"/>.</summary>
    public string? Text { get; }

    /// <summary>Absolute address or RVA, depending on <see cref="Kind"/>.</summary>
    public UInt64 Value { get; }

    /// <summary>
    /// Signed byte offset applied after resolution. Lets you anchor on a distinctive pattern and hook a few instructions into it without a second pattern.
    /// </summary>
    public int Offset { get; }

    /// <summary>Module handle for <see cref="HookTargetKind.Export"/>; <see cref="IntPtr.Zero"/> means "the region's module".</summary>
    public IntPtr Module { get; }

    /// <summary>True when resolving this target requires an AOB scan.</summary>
    public bool RequiresScan => Kind == HookTargetKind.Pattern;

    /// <summary>Scan for <paramref name="pattern"/>, then apply <paramref name="offset"/>.</summary>
    public static HookTarget FromPattern(string pattern, int offset = 0)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException("Pattern must be a non-empty AOB string.", nameof(pattern));

        return new HookTarget(HookTargetKind.Pattern, pattern, 0, offset, IntPtr.Zero);
    }

    /// <summary>Use <paramref name="address"/> verbatim.</summary>
    public static HookTarget FromAddress(UInt64 address, int offset = 0)
        => new(HookTargetKind.Address, null, address, offset, IntPtr.Zero);

    /// <summary>Resolve as <c>regionBase + rva</c>.</summary>
    public static HookTarget FromRva(UInt64 rva, int offset = 0)
        => new(HookTargetKind.RelativeVirtualAddress, null, rva, offset, IntPtr.Zero);

    /// <summary>
    /// Resolve an exported symbol.
    /// </summary>
    public static HookTarget FromExport(string exportName, IntPtr module = default, int offset = 0)
    {
        if (string.IsNullOrWhiteSpace(exportName))
            throw new ArgumentException("Export name must be non-empty.", nameof(exportName));

        return new HookTarget(HookTargetKind.Export, exportName, 0, offset, module);
    }

    /// <summary>String literals are AOB patterns.</summary>
    public static implicit operator HookTarget(string pattern) => FromPattern(pattern);

    /// <summary>Unsigned integers are absolute addresses.</summary>
    public static implicit operator HookTarget(UInt64 address) => FromAddress(address);

    /// <inheritdoc/>
    public bool Equals(HookTarget other)
        => Kind == other.Kind
        && Offset == other.Offset
        && Value == other.Value
        && Module == other.Module
        && string.Equals(Text, other.Text, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is HookTarget other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = (int)Kind;
            hash = (hash * 397) ^ Offset;
            hash = (hash * 397) ^ Value.GetHashCode();
            hash = (hash * 397) ^ (Text?.GetHashCode() ?? 0);
            return hash;
        }
    }

    /// <inheritdoc/>
    public override string ToString() => Kind switch
    {
        HookTargetKind.Pattern => $"pattern[{Text}]{FormatOffset()}",
        HookTargetKind.Address => $"0x{Value:X16}{FormatOffset()}",
        HookTargetKind.RelativeVirtualAddress => $"rva+0x{Value:X}{FormatOffset()}",
        HookTargetKind.Export => $"export[{Text}]{FormatOffset()}",
        _ => "<invalid>",
    };

    private string FormatOffset() => Offset == 0 ? string.Empty : (Offset > 0 ? $"+{Offset}" : Offset.ToString());

    public static bool operator ==(HookTarget left, HookTarget right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(HookTarget left, HookTarget right)
    {
        return !(left == right);
    }
}
