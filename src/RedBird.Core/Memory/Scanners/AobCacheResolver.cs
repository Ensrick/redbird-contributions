using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using RedBird.Core.Memory;

namespace RedBird.Core.Memory.Scanners;

/// <summary>
/// Resolves patterns through a persistent cache. Cached offsets are authoritative and are not
/// validated against the current scan region.
/// </summary>
internal sealed class AobCacheResolver
{
    private readonly ScanRegion _region;
    private readonly AobCacheOptions _options;
    private readonly ILogger _logger;
    private AobCacheDocument? _document;
    private bool _initialized;

    public AobCacheResolver(ScanRegion region, AobCacheOptions options, ILogger logger)
    {
        _region = region ?? throw new ArgumentNullException(nameof(region));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>One-shot entry point used by hook transactions.</summary>
    public static AobCacheResolution Resolve(
        ScanRegion region,
        IReadOnlyList<CompiledPattern> patterns,
        AobCacheOptions? options,
        bool allowParallel,
        ILogger logger)
        => options is null
            ? ScanFirstResults(region.Span, patterns, allowParallel)
            : new AobCacheResolver(region, options, logger).Resolve(patterns, allowParallel);

    /// <summary>Resolves the first occurrence of every pattern, scanning only cache misses.</summary>
    public AobCacheResolution Resolve(IReadOnlyList<CompiledPattern> patterns, bool allowParallel)
    {
        if (patterns.Count == 0)
            return new AobCacheResolution([], 0, 0, 0);

        EnsureInitialized();
        if (_document is null)
            return ScanFirstResults(_region.Span, patterns, allowParallel);

        long[] offsets = new long[patterns.Count];
        List<CompiledPattern> misses = [];
        List<int> missIndices = [];
        int cacheHits = 0;

        for (int i = 0; i < patterns.Count; i++)
        {
            CompiledPattern pattern = patterns[i];
            if (TryGetFirstCachedOffset(pattern, out long cachedOffset))
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
            AobCacheResolution scanned = ScanFirstResults(_region.Span, misses, allowParallel);
            scanMilliseconds = scanned.ScanMilliseconds;

            for (int i = 0; i < scanned.Offsets.Length; i++)
            {
                int resultIndex = missIndices[i];
                CompiledPattern pattern = patterns[resultIndex];
                long offset = scanned.Offsets[i];
                offsets[resultIndex] = offset;
                _document.Entries[pattern.Source] = offset;
                _document.AllEntries.Remove(pattern.Source);
            }

            Persist();
        }

        return new AobCacheResolution(offsets, cacheHits, misses.Count, scanMilliseconds);
    }

    /// <summary>Resolves every occurrence up to <paramref name="limit"/>, reusing complete cached result sets.</summary>
    public AobCacheAllResolution ResolveAll(CompiledPattern pattern, int limit)
    {
        if (pattern is null)
            throw new ArgumentNullException(nameof(pattern));
        if (limit <= 0)
            return new AobCacheAllResolution([], cacheHit: false, complete: false, scanMilliseconds: 0);

        EnsureInitialized();
        if (_document is null)
            return ScanAllResults(_region.Span, pattern, limit);

        if (_document.AllEntries.TryGetValue(pattern.Source, out long[]? cachedOffsets))
        {
            int count = Math.Min(limit, cachedOffsets.Length);
            long[] result = new long[count];
            Array.Copy(cachedOffsets, result, count);
            return new AobCacheAllResolution(result, cacheHit: true, complete: count == cachedOffsets.Length, scanMilliseconds: 0);
        }

        if (_document.Entries.TryGetValue(pattern.Source, out long cachedFirst) && cachedFirst == -1)
            return new AobCacheAllResolution([], cacheHit: true, complete: true, scanMilliseconds: 0);

        AobCacheAllResolution scanned = ScanAllResults(_region.Span, pattern, limit);
        _document.Entries[pattern.Source] = scanned.Offsets.Length == 0 ? -1 : scanned.Offsets[0];

        if (scanned.Complete)
            _document.AllEntries[pattern.Source] = scanned.Offsets;
        else
            _document.AllEntries.Remove(pattern.Source);

        Persist();
        return scanned;
    }

    private bool TryGetFirstCachedOffset(CompiledPattern pattern, out long offset)
    {
        if (_document!.Entries.TryGetValue(pattern.Source, out offset))
            return true;

        if (_document.AllEntries.TryGetValue(pattern.Source, out long[]? completeOffsets))
        {
            offset = completeOffsets.Length == 0 ? -1 : completeOffsets[0];
            return true;
        }

        offset = -1;
        return false;
    }

    private void EnsureInitialized()
    {
        if (_initialized)
            return;

        _initialized = true;
        if (_options.Store is null)
        {
            _logger.LogWarning("AOB cache {CachePath} has no store; falling back to normal scans.", _options.FilePath);
            return;
        }

        AobCacheDocument? loaded = null;

        try
        {
            loaded = _options.Store.Load(_options.FilePath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Could not read AOB cache {CachePath}; rebuilding it from normal scans.", _options.FilePath);
        }

        _document = loaded ?? new AobCacheDocument();
    }

    private void Persist()
    {
        try
        {
            _options.Store.Save(_options.FilePath, _document!);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Could not write AOB cache {CachePath}; scanning will continue without persistence.", _options.FilePath);
        }
    }

    private static AobCacheResolution ScanFirstResults(
        ReadOnlySpan<byte> region,
        IReadOnlyList<CompiledPattern> patterns,
        bool allowParallel)
    {
        if (patterns.Count == 0)
            return new AobCacheResolution([], 0, 0, 0);

        long[] offsets = new long[patterns.Count];
        Stopwatch scanWatch = Stopwatch.StartNew();
        MultiPatternScanner.FindAll(region, patterns, offsets, allowParallel);
        scanWatch.Stop();
        return new AobCacheResolution(offsets, 0, patterns.Count, scanWatch.ElapsedMilliseconds);
    }

    private static AobCacheAllResolution ScanAllResults(ReadOnlySpan<byte> region, CompiledPattern pattern, int limit)
    {
        List<long> matches = [];
        int cursor = 0;
        bool complete = false;
        Stopwatch scanWatch = Stopwatch.StartNew();

        while (matches.Count < limit && cursor < region.Length)
        {
            long found = MultiPatternScanner.FindFirst(region.Slice(cursor), pattern);
            if (found < 0)
            {
                complete = true;
                break;
            }

            long absoluteOffset = cursor + found;
            matches.Add(absoluteOffset);
            cursor = (int)absoluteOffset + 1;
        }

        if (cursor >= region.Length)
            complete = true;

        scanWatch.Stop();
        return new AobCacheAllResolution(
            matches.ToArray(),
            cacheHit: false,
            complete: complete,
            scanMilliseconds: scanWatch.ElapsedMilliseconds);
    }

}

/// <summary>Result of one cache-assisted first-match AOB resolution pass.</summary>
internal readonly struct AobCacheResolution(
    long[] offsets,
    int cacheHitCount,
    int scannedPatternCount,
    long scanMilliseconds)
{
    public long[] Offsets { get; } = offsets;
    public int CacheHitCount { get; } = cacheHitCount;
    public int ScannedPatternCount { get; } = scannedPatternCount;
    public long ScanMilliseconds { get; } = scanMilliseconds;
}

/// <summary>Result of one cache-assisted multi-match AOB resolution pass.</summary>
internal readonly struct AobCacheAllResolution(
    long[] offsets,
    bool cacheHit,
    bool complete,
    long scanMilliseconds)
{
    public long[] Offsets { get; } = offsets;
    public bool CacheHit { get; } = cacheHit;
    public bool Complete { get; } = complete;
    public long ScanMilliseconds { get; } = scanMilliseconds;
}
