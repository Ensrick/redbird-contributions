using System;

namespace RedBird.Abstractions.Hooks;

/// <summary>
/// A location to hook.
/// Implicit conversions keep existing call sites source-compatible:
/// <code>
/// tx.AddDetour(handle, "48 89 5C 24 ?? 57", callback);   // string  -> Pattern
/// tx.AddDetour(handle, 0x7FF712345678UL,    callback);   // ulong   -> Address
/// tx.AddDetour(handle, HookTarget.FromExport("DLL_ImportAIV"), callback);
/// tx.AddDetour(handle, HookTarget.FromRelativeCall("E8 ?? ?? ?? ?? 48 63 F8"), callback);
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

    /// <summary>Pattern string for scanned targets, or export name for <see cref="HookTargetKind.Export"/>.</summary>
    public string? Text { get; }

    /// <summary>Absolute address or RVA, depending on <see cref="Kind"/>.</summary>
    public UInt64 Value { get; }

    /// <summary>
    /// Signed byte offset associated with the target. For a normal pattern it is applied to the matched address. 
    /// For <see cref="HookTargetKind.RelativeCall"/> it locates the call instruction relative to the pattern match before the call destination is resolved.
    /// </summary>
    public int Offset { get; }

    /// <summary>Module handle for <see cref="HookTargetKind.Export"/>; <see cref="IntPtr.Zero"/> means "the region's module".</summary>
    public IntPtr Module { get; }

    /// <summary>True when resolving this target requires an AOB scan.</summary>
    public bool RequiresScan => Kind is HookTargetKind.Pattern or HookTargetKind.RelativeCall;

    /// <summary>Scan for <paramref name="pattern"/>, then apply <paramref name="offset"/>.</summary>
    public static HookTarget FromPattern(string pattern, int offset = 0)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException("Pattern must be a non-empty AOB string.", nameof(pattern));

        return new HookTarget(HookTargetKind.Pattern, pattern, 0, offset, IntPtr.Zero);
    }

    /// <summary>
    /// Scans for <paramref name="pattern"/>, decodes the direct relative <c>CALL</c> at <paramref name="callOffset"/> bytes from the match, and resolves the called function as the  hook target.
    /// </summary>
    /// <remarks>
    /// This is intended for functions whose entry point has no stable pattern but which have a
    /// distinctive call site. Indirect calls are rejected rather than guessed. Use
    /// <see cref="FromPattern"/> when the matched instruction itself is the intended hook site.
    /// </remarks>
    public static HookTarget FromRelativeCall(string pattern, int callOffset = 0)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException("Pattern must be a non-empty AOB string.", nameof(pattern));

        return new HookTarget(HookTargetKind.RelativeCall, pattern, 0, callOffset, IntPtr.Zero);
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
        HookTargetKind.RelativeCall => $"relative-call[pattern[{Text}]{FormatCallOffset()}]",
        HookTargetKind.Address => $"0x{Value:X16}{FormatOffset()}",
        HookTargetKind.RelativeVirtualAddress => $"rva+0x{Value:X}{FormatOffset()}",
        HookTargetKind.Export => $"export[{Text}]{FormatOffset()}",
        _ => "<invalid>",
    };

    private string FormatOffset() => Offset == 0 ? string.Empty : (Offset > 0 ? $"+{Offset}" : Offset.ToString());

    private string FormatCallOffset() => Offset == 0 ? string.Empty : $", callOffset={Offset}";

    public static bool operator ==(HookTarget left, HookTarget right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(HookTarget left, HookTarget right)
    {
        return !(left == right);
    }
}
