namespace RedBird.Abstractions.Hooks;

/// <summary>
/// Controls where the overwritten (displaced) instructions are re-emitted inside a context hook stub relative to the managed callback invocation.
/// </summary>
public enum OverwrittenInstructionPlacement
{
    /// <summary>
    /// Re-emit the overwritten instructions <b>before</b> the callback runs.
    /// The callback sees register state that already reflects the displaced logic.
    /// </summary>
    BeforeCallback,

    /// <summary>
    /// Re-emit the overwritten instructions <b>after</b> the callback returns (default).
    /// The callback runs first and may modify registers; the displaced logic then executes with the (potentially modified) register values.
    /// </summary>
    AfterCallback,

    /// <summary>
    /// Do <b>not</b> re-emit the overwritten instructions at all.
    /// Use this when the callback fully replaces the displaced logic, for example, when you are writing computed values directly into the context registers and the original instructions would immediately overwrite them.
    /// </summary>
    Suppress,
}
