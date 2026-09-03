using System;
using System.Collections.Generic;

namespace RedBird.X64.Assembly.Stateful;

/// <summary>
/// Treats several sites that hold the same logical constant as one value.
/// <para>
/// A compiler happily inlines the same magic number into a dozen places; this keeps them in step so a consumer changes one place rather than seven instructions.
/// </para>
/// </summary>
/// <typeparam name="T">Integral type of the constant.</typeparam>
public sealed class ManagedAssemblyMultiValue<T> : IAssemblyValueSite<T> where T : unmanaged
{
    private readonly object _gate = new();
    private readonly List<IAssemblyValueSite<T>> _sites = [];
    private bool _disposed;

    /// <summary>Creates an empty set.</summary>
    /// <param name="description">Label for diagnostics, ideally the constant's name in the target.</param>
    public ManagedAssemblyMultiValue(string? description = null)
        => Description = description ?? "multi-site";

    /// <summary>Creates a set from existing sites.</summary>
    public ManagedAssemblyMultiValue(IEnumerable<IAssemblyValueSite<T>> sites, string? description = null) : this(description)
    {
        foreach (IAssemblyValueSite<T> site in sites)
            Add(site);
    }

    /// <inheritdoc/>
    public string Description { get; }

    /// <summary>The individual sites, in registration order.</summary>
    public IReadOnlyList<IAssemblyValueSite<T>> Sites
    {
        get { lock (_gate) return _sites.ToArray(); }
    }

    /// <summary>Address of the first site, for diagnostics.</summary>
    public IntPtr InstructionAddress
    {
        get { lock (_gate) return _sites.Count > 0 ? _sites[0].InstructionAddress : IntPtr.Zero; }
    }

    /// <inheritdoc/>
    public T OriginalValue
    {
        get
        {
            lock (_gate)
            {
                EnsureNotEmpty();
                return _sites[0].OriginalValue;
            }
        }
    }

    /// <inheritdoc/>
    public T CurrentValue
    {
        get
        {
            lock (_gate)
            {
                EnsureNotEmpty();
                return _sites[0].CurrentValue;
            }
        }
    }

    /// <inheritdoc/>
    public int OverrideCount
    {
        get
        {
            lock (_gate)
                return _sites.Count > 0 ? _sites[0].OverrideCount : 0;
        }
    }

    /// <inheritdoc/>
    public event Action<T>? ValueChanged;

    /// <summary>
    /// Adds a site. If overrides are already in effect on the set, the newcomer is brought into line rather than being left at its original value.
    /// </summary>
    public void Add(IAssemblyValueSite<T> site)
    {
        if (site is null)
            throw new ArgumentNullException(nameof(site));

        lock (_gate)
        {
            ThrowIfDisposed();
            _sites.Add(site);
        }
    }

    /// <inheritdoc/>
    public T GetValue() => CurrentValue;

    /// <inheritdoc/>
    public AssemblyValueOverrideScope SetOverride(object owner, T value, int priority = 0, string? reason = null)
    {
        IAssemblyValueSite<T>[] sites;

        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureNotEmpty();
            sites = _sites.ToArray();
        }

        List<AssemblyValueOverrideScope> scopes = new(sites.Length);
        foreach (IAssemblyValueSite<T> site in sites)
            scopes.Add(site.SetOverride(owner, value, priority, reason));

        ValueChanged?.Invoke(value);

        return new AssemblyValueOverrideScope(() =>
        {
            foreach (AssemblyValueOverrideScope scope in scopes)
                scope.Dispose();
        });
    }

    /// <inheritdoc/>
    public void SetValue(T value)
    {
        foreach (IAssemblyValueSite<T> site in Sites)
            site.SetValue(value);

        ValueChanged?.Invoke(value);
    }

    /// <inheritdoc/>
    public void Push(T value)
    {
        foreach (IAssemblyValueSite<T> site in Sites)
            site.Push(value);

        ValueChanged?.Invoke(value);
    }

    /// <inheritdoc/>
    public T Pop()
    {
        IAssemblyValueSite<T>[] sites = (IAssemblyValueSite<T>[])Sites;
        if (sites.Length == 0)
            return default;

        foreach (IAssemblyValueSite<T> site in sites)
            site.Pop();

        T current = sites[0].CurrentValue;
        ValueChanged?.Invoke(current);
        return current;
    }

    void IAssemblyValueSite.Pop() => Pop();

    /// <inheritdoc/>
    public bool ClearOverrides(object owner)
    {
        bool any = false;
        foreach (IAssemblyValueSite<T> site in Sites)
            any |= site.ClearOverrides(owner);

        if (any)
            ValueChanged?.Invoke(CurrentValue);

        return any;
    }

    /// <inheritdoc/>
    public void RestoreOriginal()
    {
        IAssemblyValueSite<T>[] sites = (IAssemblyValueSite<T>[])Sites;
        if (sites.Length == 0)
            return;

        foreach (IAssemblyValueSite<T> site in sites)
            site.RestoreOriginal();

        ValueChanged?.Invoke(sites[0].CurrentValue);
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> DescribeOverrides()
    {
        IAssemblyValueSite<T>[] sites = (IAssemblyValueSite<T>[])Sites;
        return sites.Length == 0 ? [] : sites[0].DescribeOverrides();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        IAssemblyValueSite<T>[] sites;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            sites = _sites.ToArray();
            _sites.Clear();
        }

        foreach (IAssemblyValueSite<T> site in sites)
            site.Dispose();

        ValueChanged = null;
        GC.SuppressFinalize(this);
    }

    private void EnsureNotEmpty()
    {
        if (_sites.Count == 0)
            throw new InvalidOperationException($"'{Description}' holds no sites; nothing was resolved for it.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(Description);
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        IAssemblyValueSite<T>[] sites = (IAssemblyValueSite<T>[])Sites;
        return sites.Length == 0
            ? $"{Description} (empty)"
            : $"{Description} = {sites[0].CurrentValue} across {sites.Length} sites";
    }
}
