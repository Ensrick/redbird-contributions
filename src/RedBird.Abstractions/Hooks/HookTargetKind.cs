namespace RedBird.Abstractions.Hooks;

/// <summary>How a <see cref="HookTarget"/> resolves to an absolute address.</summary>
public enum HookTargetKind
{
    /// <summary>AOB pattern, scanned inside the transaction's region.</summary>
    Pattern,

    /// <summary>Absolute virtual address, used as-is.</summary>
    Address,

    /// <summary>Offset from the region's base address (i.e. an RVA).</summary>
    RelativeVirtualAddress,

    /// <summary>Exported symbol, resolved through the platform loader.</summary>
    Export,
}
