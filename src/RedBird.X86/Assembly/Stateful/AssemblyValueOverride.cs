using System;

namespace RedBird.X86.Assembly.Stateful;

/// <summary>
/// One party's claim on a patched value.
/// </summary>
/// <typeparam name="T">The value type of the site.</typeparam>
/// <remarks>
/// The unit of ownership in the layered model that replaces push/pop. Each override records who asked for it, so removing one is independent of the order in which the others were added.
/// </remarks>
public sealed class AssemblyValueOverride<T> where T : unmanaged
{
    internal AssemblyValueOverride(object owner, T value, int priority, long sequence, string? reason)
    {
        Owner = owner;
        Value = value;
        Priority = priority;
        Sequence = sequence;
        Reason = reason;
    }

    /// <summary>Whoever registered this override. Compared by reference identity.</summary>
    public object Owner { get; }

    /// <summary>The value this owner wants written.</summary>
    public T Value { get; internal set; }

    /// <summary>Higher wins. Ties break towards whichever override was registered later.</summary>
    public int Priority { get; }

    /// <summary>Registration order, used as the tie-breaker.</summary>
    public long Sequence { get; }

    /// <summary>Free-form note shown in diagnostics: which mod, which feature, which map.</summary>
    public string? Reason { get; }

    /// <inheritdoc/>
    public override string ToString()
        => $"{DescribeOwner(Owner)} = {Value} (priority {Priority}{(Reason is null ? "" : ", " + Reason)})";

    internal static string DescribeOwner(object owner) => owner switch
    {
        string text => text,
        Type type => type.Name,
        null => "<null>",
        _ => owner.GetType().Name,
    };
}
