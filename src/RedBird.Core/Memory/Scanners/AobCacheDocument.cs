using System.Collections.Generic;

namespace RedBird.Core.Memory.Scanners;

/// <summary>Serializable representation of an AOB-to-RVA cache.</summary>
public sealed class AobCacheDocument
{
    /// <summary>
    /// AOB pattern to region-relative virtual address mapping. A value of -1 records a pattern that was not found.
    /// </summary>
    public Dictionary<string, long> Entries { get; set; } = new(System.StringComparer.Ordinal);

    /// <summary>
    /// Complete multi-match results produced by <c>DataScanner.ScanAll</c>. Entries are omitted when a scan stopped at its result limit.
    /// </summary>
    public Dictionary<string, long[]> AllEntries { get; set; } = new(System.StringComparer.Ordinal);
}
