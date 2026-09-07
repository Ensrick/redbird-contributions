namespace RedBird.Abstractions.Hooks;

/// <summary>How a <see cref="HookTarget"/> resolves to an absolute address.</summary>
public enum HookTargetKind
{
    /// <summary>AOB pattern, scanned inside the transaction's region.</summary>
    Pattern = 0,

    /// <summary>Absolute virtual address, used as-is.</summary>
    Address = 1,

    /// <summary>Offset from the region's base address (i.e. an RVA).</summary>
    RelativeVirtualAddress = 2,

    /// <summary>Exported symbol, resolved through the platform loader.</summary>
    Export = 3,

    /// <summary>
    /// AOB pattern identifying a direct relative <c>CALL</c>; the called function,
    /// rather than the call instruction itself, is the resolved hook target.
    /// </summary>
    RelativeCall = 4,
}
