using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace RedBird.Core.Memory.Scanners;

/// <summary>Finds many AOB patterns in a single pass over the haystack.</summary>
public static class MultiPatternScanner
{
    private const int ParallelThresholdBytes = 4 * 1024 * 1024;
    private const int AnchorIsSecondFlag = 1 << 16;

    /// <summary>
    /// Locates the first occurrence of each pattern. For repeated scans, construct a
    /// <see cref="CompiledPatternSet"/> and use the corresponding overload instead.
    /// </summary>
    public static void FindAll(ReadOnlySpan<byte> haystack, IReadOnlyList<CompiledPattern> patterns, Span<long> results, bool allowParallel = true)
    {
        if (patterns is null)
            throw new ArgumentNullException(nameof(patterns));
        if (results.Length < patterns.Count)
            throw new ArgumentException("Results buffer is smaller than the pattern list.", nameof(results));

        if (patterns.Count == 0)
            return;

        if (patterns.Count == 1)
        {
            CompiledPattern pattern = patterns[0] ?? throw new ArgumentException("Pattern at index 0 is null.", nameof(patterns));
            results[0] = FindFirst(haystack, pattern);
            return;
        }

        FindAll(haystack, new CompiledPatternSet(patterns), results, allowParallel);
    }

    /// <summary>Locates the first occurrence of every pattern in a reusable compiled set.</summary>
    public static unsafe void FindAll(ReadOnlySpan<byte> haystack, CompiledPatternSet patterns, Span<long> results, bool allowParallel = true)
    {
        if (patterns is null)
            throw new ArgumentNullException(nameof(patterns));
        if (results.Length < patterns.Count)
            throw new ArgumentException("Results buffer is smaller than the pattern set.", nameof(results));

        for (int i = 0; i < patterns.Count; i++)
            results[i] = -1;

        if (patterns.Count == 0 || haystack.IsEmpty)
            return;

        if (patterns.Count == 1)
        {
            results[0] = FindFirst(haystack, patterns[0]);
            return;
        }

        AnchorIndex index = patterns.Index;

        fixed (byte* pHaystack = haystack)
        {
            int length = haystack.Length;
            bool parallel = allowParallel && length >= ParallelThresholdBytes && Environment.ProcessorCount > 1;

            if (!parallel)
            {
                fixed (long* pResults = results)
                    Sweep(pHaystack, length, 0, length, index, pResults, patterns.Count);

                return;
            }

            int workers = Math.Min(Environment.ProcessorCount, Math.Max(2, length / ParallelThresholdBytes));
            long[][] partials = new long[workers][];
            int chunk = length / workers;

            // Parallel.For completes before this fixed statement can end.
            IntPtr haystackAddress = (IntPtr)pHaystack;

            Parallel.For(0, workers, w =>
            {
                int from = w * chunk;
                int to = w == workers - 1 ? length : from + chunk;

                long[] local = new long[patterns.Count];
                for (int i = 0; i < local.Length; i++)
                    local[i] = -1;

                // Anchor positions are partitioned. Verification may read beyond "to" into the region, so no overlap window is necessary.
                fixed (long* localResults = local)
                    Sweep((byte*)haystackAddress, length, from, to, index, localResults, local.Length);

                partials[w] = local;
            });

            for (int w = 0; w < workers; w++)
            {
                long[] local = partials[w];
                for (int p = 0; p < local.Length; p++)
                {
                    if (local[p] >= 0 && (results[p] < 0 || local[p] < results[p]))
                        results[p] = local[p];
                }
            }
        }
    }

    /// <summary>
    /// Finds one pattern without constructing a multi-pattern index or allocating result buffers.
    /// </summary>
    public static unsafe long FindFirst(ReadOnlySpan<byte> haystack, CompiledPattern pattern)
    {
        if (pattern is null)
            throw new ArgumentNullException(nameof(pattern));

        int lastMatchStart = haystack.Length - pattern.Length;
        if (lastMatchStart < 0)
            return -1;

        int position = pattern.AnchorIndex;
        int endExclusive = lastMatchStart + pattern.AnchorIndex + 1;
        byte anchor = pattern.AnchorByte;

        fixed (byte* source = haystack)
        {
            if (Vector.IsHardwareAccelerated)
            {
                int width = Vector<byte>.Count;
                Vector<byte> anchorVector = new(anchor);

                while (position + width <= endExclusive)
                {
                    Vector<byte> block = Unsafe.ReadUnaligned<Vector<byte>>(source + position);
                    Vector<byte> hit = Vector.Equals(block, anchorVector);

                    if (hit != Vector<byte>.Zero)
                    {
                        for (int j = 0; j < width; j++)
                        {
                            int at = position + j;
                            if (source[at] == anchor && MatchesAtAnchor(source, at, pattern))
                                return at - pattern.AnchorIndex;
                        }
                    }

                    position += width;
                }
            }

            for (; position < endExclusive; position++)
            {
                if (source[position] == anchor && MatchesAtAnchor(source, position, pattern))
                    return position - pattern.AnchorIndex;
            }
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool MatchesAtAnchor(byte* haystack, int at, CompiledPattern pattern)
    {
        int matchStart = at - pattern.AnchorIndex;

        if (pattern.HasFingerprint)
        {
            int neighbour = pattern.FingerprintStart == pattern.AnchorIndex
                ? pattern.AnchorIndex + 1
                : pattern.AnchorIndex - 1;

            if (haystack[matchStart + neighbour] != pattern.Bytes[neighbour])
                return false;
        }

        return pattern.MatchesAtUnchecked(haystack + matchStart);
    }

    private static unsafe void Sweep(
        byte* haystack,
        int length,
        int from,
        int to,
        AnchorIndex index,
        long* results,
        int resultCount)
    {
        int remaining = resultCount;
        int position = from;

        if (index.VectorAnchors is { Length: > 0 } vectors && Vector.IsHardwareAccelerated)
        {
            int width = Vector<byte>.Count;

            while (position + width <= to)
            {
                Vector<byte> block = Unsafe.ReadUnaligned<Vector<byte>>(haystack + position);
                Vector<byte> hit = Vector.Equals(block, vectors[0]);

                for (int a = 1; a < vectors.Length; a++)
                    hit |= Vector.Equals(block, vectors[a]);

                if (hit != Vector<byte>.Zero)
                {
                    for (int j = 0; j < width; j++)
                    {
                        int at = position + j;
                        if (index.AnchorFlags[haystack[at]] != 0
                            && Verify(haystack, length, at, index, results, ref remaining))
                        {
                            return;
                        }
                    }
                }

                position += width;
            }
        }

        for (; position < to; position++)
        {
            if (index.AnchorFlags[haystack[position]] != 0
                && Verify(haystack, length, position, index, results, ref remaining))
            {
                return;
            }
        }
    }

    private static unsafe bool Verify(
        byte* haystack,
        int length,
        int at,
        AnchorIndex index,
        long* results,
        ref int remaining)
    {
        byte value = haystack[at];

        if (index.PairFirstFlags[value] != 0 && at + 1 < length)
        {
            int pair = haystack[at] | (haystack[at + 1] << 8);
            if (index.PairBucketIds.TryGetValue(pair, out int bucket)
                && VerifyBucket(haystack, length, at, index, index.PairBucketStart[bucket],
                    index.PairBucketStart[bucket + 1], index.PairBucketItems, results, ref remaining))
            {
                return true;
            }
        }

        if (index.PairSecondFlags[value] != 0 && at > 0)
        {
            int pair = haystack[at - 1] | (haystack[at] << 8) | AnchorIsSecondFlag;
            if (index.PairBucketIds.TryGetValue(pair, out int bucket)
                && VerifyBucket(haystack, length, at, index, index.PairBucketStart[bucket],
                    index.PairBucketStart[bucket + 1], index.PairBucketItems, results, ref remaining))
            {
                return true;
            }
        }

        int start = index.SingleBucketStart[value];
        int end = index.SingleBucketStart[value + 1];
        return VerifyBucket(haystack, length, at, index, start, end, index.SingleBucketItems,
            results, ref remaining);
    }

    private static unsafe bool VerifyBucket(
        byte* haystack,
        int length,
        int at,
        AnchorIndex index,
        int start,
        int end,
        int[] bucketItems,
        long* results,
        ref int remaining)
    {
        for (int b = start; b < end; b++)
        {
            int patternIndex = bucketItems[b];
            if (results[patternIndex] >= 0)
                continue;

            CompiledPattern pattern = index.Patterns[patternIndex];
            int matchStart = at - pattern.AnchorIndex;

            if (matchStart < 0 || pattern.Length > length - matchStart)
                continue;

            if (!pattern.MatchesAtUnchecked(haystack + matchStart))
                continue;

            results[patternIndex] = matchStart;
            if (--remaining == 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// CSR buckets for isolated anchors plus sparse two-byte fingerprint buckets.
    /// </summary>
    internal sealed class AnchorIndex
    {
        private const int MaxVectorAnchors = 8;

        private AnchorIndex(
            CompiledPattern[] patterns,
            int[] singleBucketStart,
            int[] singleBucketItems,
            Dictionary<int, int> pairBucketIds,
            int[] pairBucketStart,
            int[] pairBucketItems,
            byte[] pairFirstFlags,
            byte[] pairSecondFlags,
            byte[] anchorFlags,
            Vector<byte>[]? vectorAnchors)
        {
            Patterns = patterns;
            SingleBucketStart = singleBucketStart;
            SingleBucketItems = singleBucketItems;
            PairBucketIds = pairBucketIds;
            PairBucketStart = pairBucketStart;
            PairBucketItems = pairBucketItems;
            PairFirstFlags = pairFirstFlags;
            PairSecondFlags = pairSecondFlags;
            AnchorFlags = anchorFlags;
            VectorAnchors = vectorAnchors;
        }

        internal CompiledPattern[] Patterns { get; }
        internal int[] SingleBucketStart { get; }
        internal int[] SingleBucketItems { get; }
        internal Dictionary<int, int> PairBucketIds { get; }
        internal int[] PairBucketStart { get; }
        internal int[] PairBucketItems { get; }
        internal byte[] PairFirstFlags { get; }
        internal byte[] PairSecondFlags { get; }
        internal byte[] AnchorFlags { get; }
        internal Vector<byte>[]? VectorAnchors { get; }

        internal static AnchorIndex Build(IReadOnlyList<CompiledPattern> patterns)
        {
            CompiledPattern[] array;
            if (patterns is CompiledPattern[] existing)
            {
                array = existing;
            }
            else
            {
                array = new CompiledPattern[patterns.Count];
                for (int i = 0; i < patterns.Count; i++)
                    array[i] = patterns[i];
            }

            int[] singleCounts = new int[257];
            byte[] pairFirstFlags = new byte[256];
            byte[] pairSecondFlags = new byte[256];
            byte[] anchorFlags = new byte[256];
            Dictionary<int, int> pairBucketIds = new();
            List<int> pairCounts = [];

            for (int i = 0; i < array.Length; i++)
            {
                CompiledPattern pattern = array[i];
                byte anchor = pattern.AnchorByte;
                anchorFlags[anchor] = 1;

                if (!pattern.HasFingerprint)
                {
                    singleCounts[anchor + 1]++;
                    continue;
                }

                int start = pattern.FingerprintStart;
                int pair = pattern.Bytes[start] | (pattern.Bytes[start + 1] << 8);
                bool anchorIsSecond = pattern.AnchorIndex == start + 1;
                if (anchorIsSecond)
                {
                    pair |= AnchorIsSecondFlag;
                    pairSecondFlags[anchor] = 1;
                }
                else
                {
                    pairFirstFlags[anchor] = 1;
                }

                if (!pairBucketIds.TryGetValue(pair, out int bucket))
                {
                    bucket = pairBucketIds.Count;
                    pairBucketIds.Add(pair, bucket);
                    pairCounts.Add(0);
                }

                pairCounts[bucket]++;
            }

            for (int value = 0; value < 256; value++)
                singleCounts[value + 1] += singleCounts[value];

            int[] singleItems = new int[singleCounts[256]];
            int[] singleCursor = new int[256];

            int[] pairStarts = new int[pairCounts.Count + 1];
            for (int i = 0; i < pairCounts.Count; i++)
                pairStarts[i + 1] = pairStarts[i] + pairCounts[i];

            int[] pairItems = new int[pairStarts[pairCounts.Count]];
            int[] pairCursor = new int[pairCounts.Count];

            for (int i = 0; i < array.Length; i++)
            {
                CompiledPattern pattern = array[i];
                if (!pattern.HasFingerprint)
                {
                    byte anchor = pattern.AnchorByte;
                    singleItems[singleCounts[anchor] + singleCursor[anchor]++] = i;
                    continue;
                }

                int start = pattern.FingerprintStart;
                int pair = pattern.Bytes[start] | (pattern.Bytes[start + 1] << 8);
                if (pattern.AnchorIndex == start + 1)
                    pair |= AnchorIsSecondFlag;

                int bucket = pairBucketIds[pair];
                pairItems[pairStarts[bucket] + pairCursor[bucket]++] = i;
            }

            int distinct = 0;
            for (int value = 0; value < 256; value++)
            {
                if (anchorFlags[value] != 0)
                    distinct++;
            }

            Vector<byte>[]? vectors = null;
            if (distinct is > 0 and <= MaxVectorAnchors)
            {
                vectors = new Vector<byte>[distinct];
                int write = 0;
                for (int value = 0; value < 256; value++)
                {
                    if (anchorFlags[value] != 0)
                        vectors[write++] = new Vector<byte>((byte)value);
                }
            }

            return new AnchorIndex(array, singleCounts, singleItems, pairBucketIds, pairStarts, pairItems, pairFirstFlags, pairSecondFlags, anchorFlags, vectors);
        }
    }
}
