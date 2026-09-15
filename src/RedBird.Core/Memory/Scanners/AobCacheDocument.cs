using System.Collections.Generic;
using System.Runtime.Serialization;

namespace RedBird.Core.Memory.Scanners;

/// <summary>Serializable representation of an AOB-to-RVA cache.</summary>
[DataContract]
public sealed class AobCacheDocument
{
    /// <summary>Current on-disk format version.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>Checksum algorithm used by this format.</summary>
    public const string CurrentChecksumAlgorithm = "xxh64";

    /// <summary>On-disk format version.</summary>
    [DataMember(Name = "formatVersion", Order = 0)]
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>Name of the checksum algorithm used for <see cref="RegionChecksum"/>.</summary>
    [DataMember(Name = "checksumAlgorithm", Order = 1)]
    public string ChecksumAlgorithm { get; set; } = CurrentChecksumAlgorithm;

    /// <summary>Uppercase hexadecimal XXH64 checksum of the scan region.</summary>
    [DataMember(Name = "regionChecksum", Order = 2)]
    public string RegionChecksum { get; set; } = string.Empty;

    /// <summary>Length of the checksummed scan region.</summary>
    [DataMember(Name = "regionLength", Order = 3)]
    public int RegionLength { get; set; }

    /// <summary>
    /// AOB pattern to region-relative virtual address mapping. A value of -1 records a pattern that was not found.
    /// </summary>
    [DataMember(Name = "entries", Order = 4)]
    public Dictionary<string, long> Entries { get; set; } = new(System.StringComparer.Ordinal);
}
