namespace RedBird.X64.Assembly.InstructionWalker;

/// <summary>
/// Defines the starting direction for instruction traversal.
/// </summary>
public enum InstructionWalkerStartMode
{
    /// <summary>
    /// Start from the beginning of the function and proceed forward.
    /// </summary>
    Begin,

    /// <summary>
    /// Start from the end of the function (marked by INT3) and proceed backward.
    /// </summary>
    End
}
