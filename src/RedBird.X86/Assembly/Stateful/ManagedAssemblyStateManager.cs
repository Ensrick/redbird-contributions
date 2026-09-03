using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RedBird.X86.Assembly.Stateful;

/// <summary>
/// Registry of every patched constant, and the place bulk restore happens.
/// </summary>
public sealed class ManagedAssemblyStateManager(ILogger? logger = null) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StatefulAssemblyItemEntry> _entries = new(StringComparer.Ordinal);
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private bool _disposed;

    /// <summary>All registered sites, keyed by name.</summary>
    public IReadOnlyDictionary<string, StatefulAssemblyItemEntry> ManagedGlobals
    {
        get { lock (_gate) return new Dictionary<string, StatefulAssemblyItemEntry>(_entries, StringComparer.Ordinal); }
    }

    /// <summary>
    /// Registers a site under <paramref name="name"/>.
    /// </summary>
    /// <param name="name">Name used for scripting and diagnostics.</param>
    /// <param name="site">The site. Null is ignored, so a failed scan does not need a guard at the call site.</param>
    /// <param name="exposed">Whether external scripting may change it.</param>
    /// <returns>True when the site was registered.</returns>
    public bool Register(string name, IAssemblyValueSite? site, bool exposed = true)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Name must be non-empty.", nameof(name));

        if (site is null)
        {
            // A failed scan leaves the caller's field null; registering nothing is the sane outcome, and the message says which constant went missing.
            _logger.LogWarning("[{Name}] was not registered: no site was resolved for it.", name);
            return false;
        }

        lock (_gate)
        {
            ThrowIfDisposed();

            if (_entries.TryGetValue(name, out StatefulAssemblyItemEntry? existing) && !ReferenceEquals(existing.Site, site))
                _logger.LogWarning("[{Name}] is being re-registered against a different site; the previous one is no longer managed.", name);

            _entries[name] = new StatefulAssemblyItemEntry(name, site, exposed);
        }

        _logger.LogDebug("Registered [{Name}] -> {Site}.", name, site);
        return true;
    }

    /// <summary>Stops managing a site. Does not restore or dispose it.</summary>
    public bool Unregister(string name)
    {
        lock (_gate)
            return _entries.Remove(name);
    }

    /// <summary>Looks a site up by name.</summary>
    public bool TryGet(string name, out IAssemblyValueSite? site)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(name, out StatefulAssemblyItemEntry? entry))
            {
                site = entry.Site;
                return true;
            }
        }

        site = null;
        return false;
    }

    /// <summary>Looks a typed site up by name.</summary>
    public bool TryGet<T>(string name, out IAssemblyValueSite<T>? site) where T : unmanaged
    {
        site = TryGet(name, out IAssemblyValueSite? untyped) ? untyped as IAssemblyValueSite<T> : null;
        return site is not null;
    }

    /// <summary>
    /// Withdraws every claim <paramref name="owner"/> holds, across every registered site. 
    /// </summary>
    /// <returns>How many sites changed hands.</returns>
    public int ClearOwner(object owner)
    {
        if (owner is null)
            throw new ArgumentNullException(nameof(owner));

        int affected = 0;

        foreach (StatefulAssemblyItemEntry entry in Snapshot())
        {
            try
            {
                if (entry.Site.ClearOverrides(owner))
                    affected++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear overrides on [{Name}].", entry.Name);
            }
        }

        _logger.LogInformation("Cleared {Affected} sites for owner {Owner}.", affected, AssemblyValueOverride<int>.DescribeOwner(owner));
        return affected;
    }

    /// <summary>Restores every site to the value the target shipped with.</summary>
    public void RestoreAllDefaults()
    {
        foreach (StatefulAssemblyItemEntry entry in Snapshot())
        {
            try
            {
                entry.Site.RestoreOriginal();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore [{Name}].", entry.Name);
            }
        }
    }

    /// <summary>
    /// Pops one legacy stack level on every site.
    /// </summary>
    [Obsolete("Prefer ClearOwner: PopAll unwinds one level everywhere regardless of who pushed it, which only lines up if a single party pushed every site together.")]
    public void PopAll()
    {
        foreach (StatefulAssemblyItemEntry entry in Snapshot())
        {
            try
            {
                entry.Site.Pop();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to pop [{Name}].", entry.Name);
            }
        }
    }

    /// <summary>
    /// One line per site that is currently overridden, with who is holding it and why. The first
    /// thing to reach for when two mods disagree about a value.
    /// </summary>
    public IReadOnlyList<string> DescribeActiveOverrides()
    {
        List<string> lines = [];

        foreach (StatefulAssemblyItemEntry entry in Snapshot())
        {
            IReadOnlyList<string> overrides = entry.Site.DescribeOverrides();
            if (overrides.Count == 0)
                continue;

            lines.Add(entry.ToString());
            foreach (string line in overrides)
                lines.Add("    " + line);
        }

        return lines;
    }

    /// <summary>Restores and releases every registered site.</summary>
    public void Dispose()
    {
        StatefulAssemblyItemEntry[] entries;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            entries = new StatefulAssemblyItemEntry[_entries.Count];
            _entries.Values.CopyTo(entries, 0);
            _entries.Clear();
        }

        foreach (StatefulAssemblyItemEntry entry in entries)
        {
            try
            {
                entry.Site.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dispose [{Name}].", entry.Name);
            }
        }
    }

    private StatefulAssemblyItemEntry[] Snapshot()
    {
        lock (_gate)
        {
            StatefulAssemblyItemEntry[] entries = new StatefulAssemblyItemEntry[_entries.Count];
            _entries.Values.CopyTo(entries, 0);
            return entries;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ManagedAssemblyStateManager));
    }
}
