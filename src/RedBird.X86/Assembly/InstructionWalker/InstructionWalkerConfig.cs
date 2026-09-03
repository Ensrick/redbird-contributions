namespace RedBird.X86.Assembly.InstructionWalker;

/// <summary>
/// Configuration for an <see cref="InstructionWalker"/> instance.
/// </summary>
public class InstructionWalkerConfig
{
    /// <summary>
    /// Target architecture bitness. Must remain 32 in the x86 package.
    /// </summary>
    public int Bitness { get; set; } = 32;

    /// <summary>
    /// The walk rules to execute. One rule enumerates matches; several rules perform a one-shot walk that follows calls between steps.
    /// </summary>
    public InstructionWalkRule[] WalkRules { get; set; } = [];

    /// <summary>Maximum number of bytes to read for disassembly at each step.</summary>
    public int MaxReadLength { get; set; } = 4096;

    /// <summary>Maximum number of instructions to disassemble (safety limit).</summary>
    public int MaxInstructions { get; set; } = 1000;

    /// <summary>
    /// How the end of the function is recognised.
    /// <para>
    /// Defaults to <see cref="FunctionEndDetection.FirstInt3"/> to preserve the behaviour existing walk configs were written against, in particular
    /// <see cref="InstructionWalkerStartMode.End"/>, whose anchor is "the first <c>INT3</c>".
    /// Switch to <see cref="FunctionEndDetection.ReachableTerminator"/> when a walk stops short because the compiler emitted an <c>int3</c> after a <c>noreturn</c> call in the middle of the function.
    /// </para>
    /// </summary>
    public FunctionEndDetection EndDetection { get; set; } = FunctionEndDetection.FirstInt3;

    /// <summary>Builds the equivalent <see cref="FunctionScanOptions"/>.</summary>
    public FunctionScanOptions ToScanOptions() => new()
    {
        Bitness = Bitness,
        MaxBytes = MaxReadLength,
        MaxInstructions = MaxInstructions,
        EndDetection = EndDetection,

        // Walk rules anchored on End mode look for the terminator instruction itself, so padding
        // must survive into the instruction list.
        TrimTrailingPadding = false,
    };
}
