using System.Collections.Generic;

namespace RedBird.Core.Memory.Scanners;

/// <summary>Serializable representation of an AOB-to-RVA cache.</summary>
public sealed class AobCacheDocument
{
    /// <summary>Current on-disk format version.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>Checksum algorithm used by this format.</summary>
    public const string CurrentChecksumAlgorithm = "xxh64";

    /// <summary>On-disk format version.</summary>
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>Name of the checksum algorithm used for <see cref="RegionChecksum"/>.</summary>
    public string ChecksumAlgorithm { get; set; } = CurrentChecksumAlgorithm;

    /// <summary>Uppercase hexadecimal XXH64 checksum of the scan region.</summary>
    public string RegionChecksum { get; set; } = string.Empty;

    /// <summary>Length of the checksummed scan region.</summary>
    public int RegionLength { get; set; }

    /// <summary>
    /// AOB pattern to region-relative virtual address mapping. A value of -1 records a pattern that was not found.
    /// </summary>
    public Dictionary<string, long> Entries { get; set; } = new(System.StringComparer.Ordinal);

    /// <summary>
    /// Complete multi-match results produced by <c>DataScanner.ScanAll</c>. Entries are omitted when a scan stopped at its result limit.
    /// </summary>
    public Dictionary<string, long[]> AllEntries { get; set; } = new(System.StringComparer.Ordinal);
}
