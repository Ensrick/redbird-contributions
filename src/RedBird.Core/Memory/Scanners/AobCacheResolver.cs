using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;

namespace RedBird.Core.Memory.Scanners;

/// <summary>Resolves patterns through a checksum-validated cache, scanning only cache misses.</summary>
internal static class AobCacheResolver
{
    public static AobCacheResolution Resolve(ScanRegion region, IReadOnlyList<CompiledPattern> patterns, AobCacheOptions? options, bool allowParallel, ILogger logger)
    {
        if (patterns.Count == 0)
            return new AobCacheResolution([], 0, 0, 0);

        if (options is null)
            return ScanAll(region.Span, patterns, allowParallel);
        
        string checksum = XxHash64.HashToUInt64(region.Span).ToString("X16", CultureInfo.InvariantCulture);
        AobCacheDocument? document = null;

        if (options.Store is null)
        {
            logger.LogWarning("AOB cache {CachePath} has no store; falling back to a normal scan.", options.FilePath);
            return ScanAll(region.Span, patterns, allowParallel);
        }

        try
        {
            document = options.Store.Load(options.FilePath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Could not read AOB cache {CachePath}; rebuilding it from a normal scan.", options.FilePath);
        }

        AobCacheDocument activeDocument = IsUsable(document, region.Length, checksum) ? document! : CreateDocument(region.Length, checksum);

        long[] offsets = new long[patterns.Count];
        List<CompiledPattern> misses = [];
        List<int> missIndices = [];
        int cacheHits = 0;

        for (int i = 0; i < patterns.Count; i++)
        {
            CompiledPattern pattern = patterns[i];
            if (activeDocument.Entries.TryGetValue(pattern.Source, out long cachedOffset) && IsValidEntry(region.Span, pattern, cachedOffset))
            {
                offsets[i] = cachedOffset;
                cacheHits++;
                continue;
            }

            offsets[i] = -1;
            misses.Add(pattern);
            missIndices.Add(i);
        }

        long scanMilliseconds = 0;
        if (misses.Count > 0)
        {
            long[] scannedOffsets = new long[misses.Count];
            Stopwatch scanWatch = Stopwatch.StartNew();
            MultiPatternScanner.FindAll(region.Span, misses, scannedOffsets, allowParallel);
            scanWatch.Stop();
            scanMilliseconds = scanWatch.ElapsedMilliseconds;

            for (int i = 0; i < scannedOffsets.Length; i++)
            {
                int resultIndex = missIndices[i];
                long offset = scannedOffsets[i];
                offsets[resultIndex] = offset;
                activeDocument.Entries[patterns[resultIndex].Source] = offset;
            }

            try
            {
                options.Store.Save(options.FilePath, activeDocument);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogWarning(ex, "Could not write AOB cache {CachePath}; the transaction will continue without persistence.", options.FilePath);
            }
        }

        return new AobCacheResolution(offsets, cacheHits, misses.Count, scanMilliseconds);
    }

    private static AobCacheResolution ScanAll(ReadOnlySpan<byte> region, IReadOnlyList<CompiledPattern> patterns, bool allowParallel)
    {
        long[] offsets = new long[patterns.Count];
        Stopwatch scanWatch = Stopwatch.StartNew();
        MultiPatternScanner.FindAll(region, patterns, offsets, allowParallel);
        scanWatch.Stop();
        return new AobCacheResolution(offsets, 0, patterns.Count, scanWatch.ElapsedMilliseconds);
    }

    private static bool IsUsable(AobCacheDocument? document, int regionLength, string checksum)
        => document is not null &&
           document.FormatVersion == AobCacheDocument.CurrentFormatVersion &&
           string.Equals(document.ChecksumAlgorithm, AobCacheDocument.CurrentChecksumAlgorithm, StringComparison.OrdinalIgnoreCase) &&
           document.RegionLength == regionLength &&
           string.Equals(document.RegionChecksum, checksum, StringComparison.OrdinalIgnoreCase) &&
           document.Entries is not null;

    private static bool IsValidEntry(ReadOnlySpan<byte> region, CompiledPattern pattern, long offset)
    {
        if (offset == -1)
            return true;

        if (offset < 0 || offset > int.MaxValue)
            return false;

        return pattern.MatchesAt(region, (int)offset);
    }

    private static AobCacheDocument CreateDocument(int regionLength, string checksum)
        => new()
        {
            FormatVersion = AobCacheDocument.CurrentFormatVersion,
            ChecksumAlgorithm = AobCacheDocument.CurrentChecksumAlgorithm,
            RegionChecksum = checksum,
            RegionLength = regionLength,
            Entries = new Dictionary<string, long>(StringComparer.Ordinal),
        };
}

/// <summary>Result of one cache-assisted AOB resolution pass.</summary>
internal readonly struct AobCacheResolution(long[] offsets, int cacheHitCount, int scannedPatternCount, long scanMilliseconds)
{
    public long[] Offsets { get; } = offsets;
    public int CacheHitCount { get; } = cacheHitCount;
    public int ScannedPatternCount { get; } = scannedPatternCount;
    public long ScanMilliseconds { get; } = scanMilliseconds;
}
