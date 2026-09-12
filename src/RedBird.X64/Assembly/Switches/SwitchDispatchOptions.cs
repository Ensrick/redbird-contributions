using System;

namespace RedBird.X64.Assembly.Switches;

/// <summary>Safety limits and optional layout information for switch decoding.</summary>
public sealed class SwitchDispatchOptions
{
    /// <summary>Maximum bytes to inspect while looking for a supported dispatcher.</summary>
    public int MaxSearchBytes { get; init; } = 4096;

    /// <summary>Maximum instructions to inspect while looking for a supported dispatcher.</summary>
    public int MaxInstructions { get; init; } = 1000;

    /// <summary>Maximum number of selector values accepted from one dispatcher.</summary>
    public int MaxCaseCount { get; init; } = 4096;

    /// <summary>Maximum bytes decoded while finding the end of the final case/default block.</summary>
    public int MaxBoundaryBytes { get; init; } = 64 * 1024;

    /// <summary>Maximum instructions decoded while finding the end of the final block.</summary>
    public int MaxBoundaryInstructions { get; init; } = 8_000;

    /// <summary>How far backwards the decoder may search for the table-base and bounds setup.</summary>
    public int MaximumLookbackInstructions { get; init; } = 16;

    /// <summary>
    /// Optional known exclusive end of the containing function. Zero requests automatic
    /// detection from the final case/default block.
    /// </summary>
    public UInt64 FunctionEndAddressExclusive { get; init; }
}
