using System;
using System.IO;

namespace RedBird.Core.Memory.Scanners;

/// <summary>Configures persistent AOB-to-RVA resolution for hook transactions.</summary>
public sealed record AobCacheOptions
{
    /// <summary>
    /// Creates cache options for one scan region. The parent directory is created when the cache is first written.
    /// </summary>
    /// <param name="filePath">Path of the cache file. Use a distinct file for each module or scan region.</param>
    public AobCacheOptions(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("The AOB cache file path cannot be empty.", nameof(filePath));

        FilePath = Path.GetFullPath(filePath);
    }

    /// <summary>Absolute path of the cache file.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Persistence implementation. The built-in store writes compact JSON; replace it to use MessagePack or another format.
    /// </summary>
    public IAobCacheStore Store { get; init; } = JsonAobCacheStore.Instance;
}
