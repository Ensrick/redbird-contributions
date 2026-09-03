namespace RedBird.X64.Hooks;

/// <summary>
/// Describes one patch point to inject into the cloned function.
/// </summary>
public sealed class FunctionClonePatch
{
    /// <summary>Diagnostic label, shown in logs alongside the sites this patch matched.</summary>
    public string Name { get; init; } = "patch";

    /// <summary>Which instructions in the clone should trigger this patch.</summary>
    public required FunctionClonePatchPredicate Predicate { get; init; }

    /// <summary>Code to emit before (or instead of) the matched instruction.</summary>
    public required FunctionClonePatchSite Generator { get; init; }

    /// <summary>
    /// Wrap the generated code in <c>pushfq</c>/<c>popfq</c>.
    /// <para>
    /// Defaults to <see langword="true"/>, a easy way to break a clone hook is to inject at a site that sits between a <c>cmp</c> and the <c>jcc</c> that reads
    /// its result: almost any generated code clobbers EFLAGS and the branch then goes the wrong way, intermittently and far from the injection point. 
    /// Turn it off only when the generated code provably leaves flags alone, or when the patch is meant to change them.
    /// </para>
    /// <para>
    /// Note that this shifts <c>rsp</c> by 8 for the duration of the generated code, on top of the 8 bytes the <c>call</c> into the island already pushed, relevant if the generator reads stack arguments relative to <c>rsp</c>.
    /// </para>
    /// </summary>
    public bool PreserveFlags { get; init; } = true;
}
