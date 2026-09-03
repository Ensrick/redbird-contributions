namespace RedBird.X64.Assembly;

/// <summary>Limits and strategy for <see cref="FunctionBoundary.Disassemble"/>.</summary>
public sealed class FunctionScanOptions
{
    /// <summary>Shared defaults.</summary>
    public static FunctionScanOptions Default { get; } = new();

    /// <summary>
    /// Target architecture bitness. Must remain 64 in the x64 package.
    /// </summary>
    public int Bitness { get; init; } = 64;

    /// <summary>Hard ceiling on bytes read from the target.</summary>
    public int MaxBytes { get; init; } = 64 * 1024;

    /// <summary>Hard ceiling on decoded instructions.</summary>
    public int MaxInstructions { get; init; } = 8_000;

    /// <summary>Termination strategy.</summary>
    public FunctionEndDetection EndDetection { get; init; } = FunctionEndDetection.ReachableTerminator;

    /// <summary>Drop trailing <c>INT3</c>/<c>NOP</c> alignment padding from the result.</summary>
    public bool TrimTrailingPadding { get; init; } = true;
}
