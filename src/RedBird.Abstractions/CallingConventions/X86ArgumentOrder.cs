namespace RedBird.Abstractions.CallingConventions;


/// <summary>Order in which x86 stack arguments are pushed.</summary>
public enum X86ArgumentOrder
{
    /// <summary>The last argument is pushed first.</summary>
    RightToLeft = 0,

    /// <summary>The first argument is pushed first.</summary>
    LeftToRight = 1,
}
