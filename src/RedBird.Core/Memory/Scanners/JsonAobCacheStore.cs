using System;
using System.IO;
using System.Runtime.Serialization.Json;

namespace RedBird.Core.Memory.Scanners;

/// <summary>Compact JSON implementation of <see cref="IAobCacheStore"/>.</summary>
public sealed class JsonAobCacheStore : IAobCacheStore
{
    /// <summary>Shared stateless store instance.</summary>
    public static JsonAobCacheStore Instance { get; } = new();

    private JsonAobCacheStore() { }

    /// <inheritdoc/>
    public AobCacheDocument? Load(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return CreateSerializer().ReadObject(stream) as AobCacheDocument;
    }

    /// <inheritdoc/>
    public void Save(string filePath, AobCacheDocument document)
    {
        if (document is null)
            throw new ArgumentNullException(nameof(document));

        string fullPath = Path.GetFullPath(filePath);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);

        string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                CreateSerializer().WriteObject(stream, document);
                stream.Flush();
            }

            if (File.Exists(fullPath))
                File.Replace(temporaryPath, fullPath, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, fullPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static DataContractJsonSerializer CreateSerializer()
        => new(typeof(AobCacheDocument), new DataContractJsonSerializerSettings
        {
            UseSimpleDictionaryFormat = true,
        });
}
