namespace RedBird.Core.Memory.Scanners;

/// <summary>Loads and saves plain persistent AOB cache documents.</summary>
/// <remarks>
/// Implement this interface to use a format such as MessagePack. Implementations should replace existing files atomically when possible.
/// </remarks>
public interface IAobCacheStore
{
    /// <summary>Loads a cache document, or returns <see langword="null"/> when the file does not exist.</summary>
    AobCacheDocument? Load(string filePath);

    /// <summary>Saves a complete cache document.</summary>
    void Save(string filePath, AobCacheDocument document);
}
