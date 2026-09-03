using RedBird.Core.Memory;
using RedBird.Core.Memory.Scanners;
using RedBird.Core.Platform;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace RedBird.X64.Tests;

/// <summary>
/// Tests for <see cref="CompiledPattern"/>, <see cref="MultiPatternScanner"/> and
/// <see cref="ScanRegion"/>: the layer everything else resolves addresses through.
/// </summary>
/// <remarks>
/// Pure functions over byte arrays, so unlike the detour tests these need no executable memory and run anywhere.
/// </remarks>
public sealed class PatternScanningTests
{
    private const UInt64 TestBase = 0x0000_7FF7_1000_0000;

    private static ScanRegion Region(byte[] bytes) => ScanRegion.FromMemory(bytes, TestBase, "test");

    // -------------------------------------------------------------------------
    // Parsing
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("48 89 5C 24", 4)]
    [InlineData("48 89 5C 24 ?? 57", 6)]
    [InlineData("48 ? 5C ?? 57", 5)]
    [InlineData("48,89,5C", 3)]
    public void Parse_AcceptsWildcardsAndSeparators(string pattern, int expectedLength)
    {
        CompiledPattern compiled = CompiledPattern.Parse(pattern);

        Assert.Equal(expectedLength, compiled.Length);
        Assert.Equal(pattern, compiled.Source);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("48 ZZ 5C")]      // not hex, not a wildcard
    [InlineData("48 899 5C")]     // three nibbles
    [InlineData("?? ?? ??")]      // nothing to anchor on
    public void Parse_RejectsMalformedPatterns(string pattern)
    {
        Assert.Throws<ArgumentException>(() => CompiledPattern.Parse(pattern));
    }

    [Fact]
    public void Parse_MarksWildcardPositionsAsUnmasked()
    {
        CompiledPattern compiled = CompiledPattern.Parse("48 ?? 5C");

        Assert.Equal(0xFF, compiled.Mask[0]);
        Assert.Equal(0x00, compiled.Mask[1]);
        Assert.Equal(0xFF, compiled.Mask[2]);
    }

    // -------------------------------------------------------------------------
    // Matching
    // -------------------------------------------------------------------------

    [Fact]
    public void FindFirst_LocatesAnExactMatch()
    {
        byte[] haystack = [0x00, 0x11, 0x48, 0x89, 0x5C, 0x24, 0x20, 0xFF];

        long offset = MultiPatternScanner.FindFirst(haystack, CompiledPattern.Parse("48 89 5C 24"));

        Assert.Equal(2, offset);
    }

    [Fact]
    public void FindFirst_HonoursWildcards()
    {
        byte[] haystack = [0x48, 0x89, 0x5C, 0x24, 0x7A, 0x57];

        long offset = MultiPatternScanner.FindFirst(haystack, CompiledPattern.Parse("48 89 5C 24 ?? 57"));

        Assert.Equal(0, offset);
    }

    [Fact]
    public void FindFirst_ReturnsMinusOneWhenAbsent()
    {
        byte[] haystack = [0x48, 0x89, 0x5C, 0x24];

        Assert.Equal(-1, MultiPatternScanner.FindFirst(haystack, CompiledPattern.Parse("DE AD BE EF")));
    }

    [Fact]
    public void FindFirst_ReturnsTheFirstOfSeveralMatches()
    {
        byte[] haystack = [0x90, 0xDE, 0xAD, 0x90, 0x90, 0xDE, 0xAD];

        Assert.Equal(1, MultiPatternScanner.FindFirst(haystack, CompiledPattern.Parse("DE AD")));
    }

    [Fact]
    public void FindFirst_DoesNotMatchAcrossTheEndOfTheRegion()
    {
        byte[] haystack = [0x11, 0x22, 0xDE, 0xAD];

        Assert.Equal(-1, MultiPatternScanner.FindFirst(haystack, CompiledPattern.Parse("DE AD BE EF")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    public void FindFirst_MaskedWordVerifierHandlesEveryTailLength(int patternLength)
    {
        byte[] haystack = new byte[96];
        Unsafe.InitBlockUnaligned(ref haystack[0], 0xCC, (uint)haystack.Length);

        byte[] expected = new byte[patternLength];
        string[] tokens = new string[patternLength];
        for (int i = 0; i < patternLength; i++)
        {
            expected[i] = (byte)(0x10 + i);
            tokens[i] = expected[i].ToString("X2");
        }

        if (patternLength > 1)
            tokens[patternLength / 2] = "??";

        const int plantedAt = 37;
        Array.Copy(expected, 0, haystack, plantedAt, expected.Length);
        if (patternLength > 1)
            haystack[plantedAt + (patternLength / 2)] = 0xFE;

        CompiledPattern pattern = CompiledPattern.Parse(string.Join(" ", tokens));
        Assert.Equal(plantedAt, MultiPatternScanner.FindFirst(haystack, pattern));
        Assert.True(pattern.MatchesAt(haystack, plantedAt));
    }

    [Fact]
    public void FindAll_ResolvesEveryPatternInOnePass()
    {
        byte[] haystack = new byte[8192];
        haystack[100] = 0xDE; haystack[101] = 0xAD;
        haystack[4000] = 0xBE; haystack[4001] = 0xEF;
        haystack[8000] = 0xCA; haystack[8001] = 0xFE;

        List<CompiledPattern> patterns =
        [
            CompiledPattern.Parse("DE AD"),
            CompiledPattern.Parse("BE EF"),
            CompiledPattern.Parse("CA FE"),
            CompiledPattern.Parse("11 22 33 44"),   // absent
        ];

        long[] results = new long[patterns.Count];
        MultiPatternScanner.FindAll(haystack, patterns, results);

        Assert.Equal(100, results[0]);
        Assert.Equal(4000, results[1]);
        Assert.Equal(8000, results[2]);
        Assert.Equal(-1, results[3]);
    }

    [Fact]
    public void FindAll_ReusableSetHandlesBothFingerprintOrientationsAndFallback()
    {
        byte[] first = new byte[256];
        byte[] second = new byte[256];

        // Noise contains the rare seed but not its neighbouring fingerprint byte.
        first[5] = 0xB7;
        second[7] = 0xB7;

        byte[][] values =
        [
            [0xB7, 0x44, 0x91, 0x10], // selected anchor is first in the fingerprint
            [0x44, 0xB7, 0x92, 0x10], // selected anchor is second in the fingerprint
            [0xB7, 0x93, 0x44],       // anchor is isolated by the wildcard
        ];
        int[] firstOffsets = [40, 90, 140];
        int[] secondOffsets = [20, 80, 180];

        for (int i = 0; i < values.Length; i++)
        {
            Array.Copy(values[i], 0, first, firstOffsets[i], values[i].Length);
            Array.Copy(values[i], 0, second, secondOffsets[i], values[i].Length);
        }

        CompiledPatternSet set = new([
            CompiledPattern.Parse("B7 44 ?? 10"),
            CompiledPattern.Parse("44 B7 ?? 10"),
            CompiledPattern.Parse("B7 ?? 44"),
        ]);

        long[] results = new long[set.Count];
        MultiPatternScanner.FindAll(first, set, results, allowParallel: false);
        Assert.Equal(Array.ConvertAll(firstOffsets, static value => (long)value), results);

        MultiPatternScanner.FindAll(second, set, results, allowParallel: false);
        Assert.Equal(Array.ConvertAll(secondOffsets, static value => (long)value), results);
    }

    /// <summary>
    /// Naive reference: the simplest correct first-match scan, used to check the real one.
    /// </summary>
    private static long ReferenceFindFirst(ReadOnlySpan<byte> haystack, CompiledPattern pattern)
    {
        for (int start = 0; start + pattern.Length <= haystack.Length; start++)
        {
            bool matched = true;

            for (int k = 0; k < pattern.Length; k++)
            {
                if (((haystack[start + k] ^ pattern.Bytes[k]) & pattern.Mask[k]) != 0)
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return start;
        }

        return -1;
    }

    [Fact]
    public void FindAll_MatchesAReferenceScanAcrossPartitionBoundaries()
    {
        // Large enough to cross the parallel threshold, so the partitioned sweep is what runs.
        const int Length = 6 * 1024 * 1024;

        byte[] haystack = new byte[Length];
        new Random(20260812).NextBytes(haystack);
        const int PatternLength = 5;

        // The scanner partitions by dividing the region between workers, so the exact boundaries depend on the worker count. Planting around several plausible fractions means at least one
        // match straddles a real boundary whatever that count turns out to be, without the test having to know the partitioning rule.
        List<int> offsets = [];

        foreach (int boundary in new[] { Length / 2, Length / 3, Length / 4, (Length * 2) / 3 })
        {
            offsets.Add(boundary - 2);   // straddles the boundary
            offsets.Add(boundary + 16);  // just past it
        }

        offsets.Add(1024);
        offsets.Add(Length - 64);
        offsets.Sort();

        // Every plant must be clear of its neighbours, or a later one silently overwrites an earlier one's tail and the pattern it was planted for is simply not there any more.
        for (int i = 1; i < offsets.Count; i++)
        {
            Assert.True(
                offsets[i] - offsets[i - 1] >= PatternLength,
                $"Planted offsets {offsets[i - 1]} and {offsets[i]} overlap for a {PatternLength}-byte pattern.");
        }

        List<CompiledPattern> patterns = [];

        for (int i = 0; i < offsets.Count; i++)
        {
            int at = offsets[i];

            haystack[at] = 0xC7;
            haystack[at + 1] = 0x81;
            haystack[at + 2] = 0x5A;
            haystack[at + 3] = 0x33;
            haystack[at + 4] = (byte)i;

            patterns.Add(CompiledPattern.Parse($"C7 81 5A 33 {i:X2}"));
        }

        long[] parallel = new long[patterns.Count];
        MultiPatternScanner.FindAll(haystack, patterns, parallel, allowParallel: true);

        long[] serial = new long[patterns.Count];
        MultiPatternScanner.FindAll(haystack, patterns, serial, allowParallel: false);

        long[] reference = new long[patterns.Count];
        for (int i = 0; i < patterns.Count; i++)
            reference[i] = ReferenceFindFirst(haystack, patterns[i]);

        // Partitioning must not change the answer, and neither may the anchor prefilter.
        Assert.Equal(reference, serial);
        Assert.Equal(reference, parallel);

        // And the answer should be where we put it, if this fails while the three above pass, the random fill produced an earlier match rather than the scanner being wrong.
        Assert.Equal(offsets.ToArray(), Array.ConvertAll(parallel, static value => (int)value));
    }

    [Fact]
    public void AnchorSelection_FindsPatternsThatStartWithCommonBytes()
    {
        // Every pattern in a real project starts with a REX prefix like 0x48, which occurs every few bytes in compiled code. The scanner anchors on a rarer literal further in; this asserts the
        // result is still correct, not which byte it picked, so retuning the frequency table cannot break the test.
        byte[] haystack = new byte[64 * 1024];

        for (int i = 0; i < haystack.Length; i++)
            haystack[i] = (i % 3) == 0 ? (byte)0x48 : (byte)0x8B;

        byte[] needle = [0x48, 0x89, 0x5C, 0x24, 0x08, 0x0F, 0xB7, 0x44];
        int at = 40000;
        Array.Copy(needle, 0, haystack, at, needle.Length);

        long offset = MultiPatternScanner.FindFirst(haystack, CompiledPattern.Parse("48 89 5C 24 ?? 0F B7 44"));

        Assert.Equal(at, offset);
    }

    [Fact]
    public void FindAll_RejectsAnUndersizedResultsBuffer()
    {
        List<CompiledPattern> patterns = [CompiledPattern.Parse("90"), CompiledPattern.Parse("CC")];
        long[] tooSmall = new long[1];

        Assert.Throws<ArgumentException>(() =>
        {
            MultiPatternScanner.FindAll(new byte[16], patterns, tooSmall);
        });
    }

    // -------------------------------------------------------------------------
    // ScanRegion
    // -------------------------------------------------------------------------

    [Fact]
    public void ScanRegion_TranslatesOffsetsToAddresses()
    {
        using ScanRegion region = Region([0x00, 0x11, 0x22, 0x33]);

        Assert.Equal(TestBase, region.BaseAddress);
        Assert.Equal(4, region.Length);
        Assert.Equal(TestBase + 2, region.ToAddress(2));
    }

    [Fact]
    public void ScanRegion_FromMemory_DoesNotCopy()
    {
        byte[] buffer = [0xAA, 0xBB, 0xCC];
        using ScanRegion region = Region(buffer);

        // Mutating the source must be visible through the region: the whole reason FromMemory exists is that Copy allocated a second image of the module per transaction.
        buffer[1] = 0xFF;

        Assert.Equal(0xFF, region.Span[1]);
    }

    [Fact]
    public void ScanRegion_Copy_Snapshots()
    {
        byte[] buffer = [0xAA, 0xBB, 0xCC];
        using ScanRegion region = ScanRegion.Copy(buffer, TestBase);

        buffer[1] = 0xFF;

        Assert.Equal(0xBB, region.Span[1]);
    }

    [Fact]
    public unsafe void ScanRegion_FromPointer_WrapsLiveMemory()
    {
        IntPtr block = Marshal.AllocHGlobal(64);

        try
        {
            for (int i = 0; i < 64; i++)
                ((byte*)block)[i] = (byte)i;

            using ScanRegion region = ScanRegion.FromPointer(block, 64, "live");

            Assert.Equal((UInt64)block.ToInt64(), region.BaseAddress);
            Assert.Equal(7, region.Span[7]);

            long offset = MultiPatternScanner.FindFirst(region.Span, CompiledPattern.Parse("0A 0B 0C"));
            Assert.Equal(10, offset);
            Assert.Equal((UInt64)block.ToInt64() + 10, region.ToAddress(offset));
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    [Fact]
    public void ScanRegion_FromPointer_RejectsNonsense()
    {
        Assert.Throws<ArgumentNullException>(() => ScanRegion.FromPointer(IntPtr.Zero, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScanRegion.FromPointer(new IntPtr(0x1000), 0));
    }

    [Fact]
    public unsafe void ModuleImageLayout_ReturnsOnlyExecutablePeSections()
    {
        byte[] image = new byte[0x3000];

        fixed (byte* start = image)
        {
            start[0] = 0x4D;
            start[1] = 0x5A;
            *(int*)(start + 0x3C) = 0x80;

            byte* nt = start + 0x80;
            *(uint*)nt = 0x0000_4550;
            *(ushort*)(nt + 6) = 2;
            *(ushort*)(nt + 20) = 0xF0;
            *(int*)(nt + 24 + 56) = image.Length;

            byte* text = nt + 24 + 0xF0;
            text[0] = (byte)'.'; text[1] = (byte)'t'; text[2] = (byte)'e';
            text[3] = (byte)'x'; text[4] = (byte)'t';
            *(uint*)(text + 8) = 0x600;
            *(uint*)(text + 12) = 0x1000;
            *(uint*)(text + 16) = 0x600;
            *(uint*)(text + 36) = 0x6000_0020; // code, execute, read

            byte* readOnlyData = text + 40;
            readOnlyData[0] = (byte)'.'; readOnlyData[1] = (byte)'r';
            readOnlyData[2] = (byte)'d'; readOnlyData[3] = (byte)'a';
            readOnlyData[4] = (byte)'t'; readOnlyData[5] = (byte)'a';
            *(uint*)(readOnlyData + 8) = 0x400;
            *(uint*)(readOnlyData + 12) = 0x2000;
            *(uint*)(readOnlyData + 16) = 0x400;
            *(uint*)(readOnlyData + 36) = 0x4000_0040; // initialized data, read

            image[0x1010] = 0xDE;
            image[0x1011] = 0xAD;

            IReadOnlyList<ModuleImageRegion> regions =
                ModuleImageLayout.GetExecutableRegions((IntPtr)start);

            ModuleImageRegion region = Assert.Single(regions);
            Assert.Equal(".text", region.Name);
            Assert.Equal(0x600, region.Length);
            Assert.Equal((IntPtr)(start + 0x1000), region.Address);
            Assert.Equal(0x10, MultiPatternScanner.FindFirst(
                region.Span, CompiledPattern.Parse("DE AD")));
        }
    }
}
