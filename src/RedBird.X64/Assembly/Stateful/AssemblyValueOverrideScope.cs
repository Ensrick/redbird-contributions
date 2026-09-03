using System;

namespace RedBird.X64.Assembly.Stateful;

/// <summary>
/// Removes an override when disposed, so a scoped change can be written as a <c>using</c> block.
/// </summary>
public sealed class AssemblyValueOverrideScope : IDisposable
{
    private Action? _release;

    internal AssemblyValueOverrideScope(Action release) => _release = release;

    /// <summary>Withdraws the override and re-evaluates the site.</summary>
    public void Dispose()
    {
        Action? release = _release;
        _release = null;
        release?.Invoke();
    }
}
