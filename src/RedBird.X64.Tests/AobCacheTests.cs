using Microsoft.Extensions.Logging.Abstractions;
using RedBird.Core.Memory;
using RedBird.Core.Memory.Scanners;
using RedBird.X64.Memory.Scanners;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using Xunit;

namespace RedBird.X64.Tests;

public sealed class AobCacheTests
{
    [Fact]
    public void XxHash64_MatchesThePublishedEmptyInputVector()
        => Assert.Equal(0xEF46DB3751D8E999UL, XxHash64.HashToUInt64(ReadOnlySpan<byte>.Empty));

    [Fact]
    public void Resolve_FirstRunWritesJsonAndSecondRunSkipsTheScan()
    {
        string cachePath = CreateCachePath();
        try
        {
            byte[] bytes = [0x90, 0x48, 0x89, 0x5C, 0x24, 0x10, 0x57, 0xC3];
            CompiledPattern[] patterns = [CompiledPattern.Parse("48 89 5C 24 ?? 57")];
            AobCacheOptions options = new(cachePath);

            using (ScanRegion firstRegion = ScanRegion.FromMemory(bytes, 0x1000))
            {
                AobCacheResolution first = AobCacheResolver.Resolve(firstRegion, patterns, options, allowParallel: false, NullLogger.Instance);

                Assert.Equal(1, first.Offsets[0]);
                Assert.Equal(1, first.ScannedPatternCount);
                Assert.Equal(0, first.CacheHitCount);
            }

            Assert.True(File.Exists(cachePath));
            string json = File.ReadAllText(cachePath);
            Assert.Contains("\"regionChecksum\"", json, StringComparison.Ordinal);
            Assert.Contains("48 89 5C 24 ?? 57", json, StringComparison.Ordinal);

            // A different image base proves that the persisted value is an RVA, not an address.
            using ScanRegion secondRegion = ScanRegion.FromMemory(bytes, 0x7FFF_0000);
            AobCacheResolution second = AobCacheResolver.Resolve(secondRegion, patterns, options, allowParallel: false, NullLogger.Instance);

            Assert.Equal(1, second.Offsets[0]);
            Assert.Equal(0, second.ScannedPatternCount);
            Assert.Equal(1, second.CacheHitCount);
            Assert.Equal(0, second.ScanMilliseconds);
        }
        finally
        {
            DeleteCacheDirectory(cachePath);
        }
    }

    [Fact]
    public void Resolve_ChangedRegionChecksumInvalidatesAndRebuildsTheCache()
    {
        string cachePath = CreateCachePath();
        try
        {
            byte[] bytes = [0x90, 0xAA, 0xBB, 0xCC, 0x90, 0x90, 0x90, 0x90];
            CompiledPattern[] patterns = [CompiledPattern.Parse("AA BB CC")];
            AobCacheOptions options = new(cachePath);

            using ScanRegion region = ScanRegion.FromMemory(bytes, 0x1000);
            AobCacheResolution first = AobCacheResolver.Resolve(region, patterns, options, allowParallel: false, NullLogger.Instance);
            Assert.Equal(1, first.Offsets[0]);

            bytes[1] = 0x90;
            bytes[2] = 0x90;
            bytes[3] = 0x90;
            bytes[4] = 0xAA;
            bytes[5] = 0xBB;
            bytes[6] = 0xCC;

            AobCacheResolution changed = AobCacheResolver.Resolve(region, patterns, options, allowParallel: false, NullLogger.Instance);

            Assert.Equal(4, changed.Offsets[0]);
            Assert.Equal(1, changed.ScannedPatternCount);
            Assert.Equal(0, changed.CacheHitCount);
        }
        finally
        {
            DeleteCacheDirectory(cachePath);
        }
    }

    [Fact]
    public void Resolve_CachesMissingPatterns()
    {
        string cachePath = CreateCachePath();
        try
        {
            using ScanRegion region = ScanRegion.FromMemory(new byte[] { 0x90, 0x90, 0xC3 }, 0x1000);
            CompiledPattern[] patterns = [CompiledPattern.Parse("DE AD BE EF")];
            AobCacheOptions options = new(cachePath);

            AobCacheResolution first = AobCacheResolver.Resolve(region, patterns, options, allowParallel: false, NullLogger.Instance);
            AobCacheResolution second = AobCacheResolver.Resolve(region, patterns, options, allowParallel: false, NullLogger.Instance);

            Assert.Equal(-1, first.Offsets[0]);
            Assert.Equal(1, first.ScannedPatternCount);
            Assert.Equal(-1, second.Offsets[0]);
            Assert.Equal(0, second.ScannedPatternCount);
            Assert.Equal(1, second.CacheHitCount);
        }
        finally
        {
            DeleteCacheDirectory(cachePath);
        }
    }

    [Fact]
    public void Resolve_CustomStoreFailuresFallBackToTheScanner()
    {
        using ScanRegion region = ScanRegion.FromMemory(new byte[] { 0x90, 0xAA, 0xBB, 0xC3 }, 0x1000);
        CompiledPattern[] patterns = [CompiledPattern.Parse("AA BB")];
        AobCacheOptions options = new("unused.aobcache")
        {
            Store = new ThrowingCacheStore(),
        };

        AobCacheResolution result = AobCacheResolver.Resolve(region, patterns, options, allowParallel: false, NullLogger.Instance);

        Assert.Equal(1, result.Offsets[0]);
        Assert.Equal(1, result.ScannedPatternCount);
        Assert.Equal(0, result.CacheHitCount);
    }

    [Fact]
    public void DataScanner_ScanPersistsAndReusesTheFirstMatch()
    {
        byte[] bytes = [0x90, 0xAA, 0xBB, 0xCC, 0xC3];
        MemoryCacheStore store = new();
        AobCacheOptions options = new("unused.aobcache") { Store = store };

        using (ScanRegion firstRegion = ScanRegion.FromMemory(bytes, 0x1000))
        {
            DataScanner first = DataScanner.Create(firstRegion, logger: null, aobCache: options)
                .Scan("AA BB CC");

            Assert.True(first.Found);
            Assert.Equal(1, first.CurrentOffset);
            Assert.Equal(1, store.SaveCount);
        }

        using ScanRegion rebasedRegion = ScanRegion.FromMemory(bytes, 0x7FFF_0000);
        DataScanner second = DataScanner.Create(rebasedRegion, logger: null, aobCache: options)
            .Scan("AA BB CC", offset: 2);

        Assert.True(second.Found);
        Assert.Equal(3, second.CurrentOffset);
        Assert.Equal(2, store.LoadCount);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public void MultiPatternScanner_CachedOverloadsScanOnlyMisses()
    {
        using ScanRegion region = ScanRegion.FromMemory(new byte[] { 0x90, 0xAA, 0xBB, 0xC3 }, 0x1000);
        CompiledPattern found = CompiledPattern.Parse("AA BB");
        CompiledPattern missing = CompiledPattern.Parse("DE AD");
        MemoryCacheStore store = new();
        AobCacheOptions options = new("unused.aobcache") { Store = store };

        Assert.Equal(1, MultiPatternScanner.FindFirst(region, found, options));
        Assert.Equal(1, store.SaveCount);

        CompiledPattern[] patterns = [found, missing];
        long[] results = new long[patterns.Length];
        MultiPatternScanner.FindAll(region, patterns, results, options, allowParallel: false);

        Assert.Equal([1L, -1L], results);
        Assert.Equal(2, store.SaveCount);

        MultiPatternScanner.FindAll(region, patterns, results, options, allowParallel: false);
        Assert.Equal([1L, -1L], results);
        Assert.Equal(2, store.SaveCount);
    }

    [Fact]
    public void DataScanner_ScanAllCachesOnlyCompleteResultSets()
    {
        byte[] bytes = [0xAA, 0xBB, 0x90, 0xAA, 0xBB, 0xC3];
        MemoryCacheStore store = new();
        AobCacheOptions options = new("unused.aobcache") { Store = store };

        using ScanRegion region = ScanRegion.FromMemory(bytes, 0x1000);

        IReadOnlyList<UInt64> truncated = DataScanner
            .Create(region, logger: null, aobCache: options)
            .ScanAll("AA BB", limit: 1);

        Assert.Equal([0x1000UL], truncated);
        Assert.Empty(store.Document!.AllEntries);
        Assert.Equal(1, store.SaveCount);

        IReadOnlyList<UInt64> complete = DataScanner
            .Create(region, logger: null, aobCache: options)
            .ScanAll("AA BB");

        Assert.Equal([0x1000UL, 0x1003UL], complete);
        Assert.Equal([0L, 3L], store.Document!.AllEntries["AA BB"]);
        Assert.Equal(2, store.SaveCount);

        IReadOnlyList<UInt64> cached = DataScanner
            .Create(region, logger: null, aobCache: options)
            .ScanAll("AA BB");

        Assert.Equal(complete, cached);
        Assert.Equal(3, store.LoadCount);
        Assert.Equal(2, store.SaveCount);
    }

    private static string CreateCachePath()
        => Path.Combine(Path.GetTempPath(), "redbird-aob-cache-tests", Guid.NewGuid().ToString("N"), "fixture.aobcache.json");

    private static void DeleteCacheDirectory(string cachePath)
    {
        string? directory = Path.GetDirectoryName(cachePath);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class ThrowingCacheStore : IAobCacheStore
    {
        public AobCacheDocument? Load(string filePath)
            => throw new IOException("Synthetic read failure.");

        public void Save(string filePath, AobCacheDocument document)
            => throw new IOException("Synthetic write failure.");
    }

    private sealed class MemoryCacheStore : IAobCacheStore
    {
        public AobCacheDocument? Document { get; private set; }
        public int LoadCount { get; private set; }
        public int SaveCount { get; private set; }

        public AobCacheDocument? Load(string filePath)
        {
            LoadCount++;
            return Document;
        }

        public void Save(string filePath, AobCacheDocument document)
        {
            SaveCount++;
            Document = document;
        }
    }
}
