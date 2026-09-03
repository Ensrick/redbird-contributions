namespace RedBird.Abstractions.CallingConventions;

/// <summary>Identifies which side removes x86 arguments placed on the stack.</summary>
public enum X86StackCleanup
{
    /// <summary>The caller removes stack arguments after the call.</summary>
    Caller = 0,

    /// <summary>The callee removes stack arguments as it returns.</summary>
    Callee = 1,
}
