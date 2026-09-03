using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace RedBird.Core.Memory.Scanners;

/// <summary>
/// An AOB pattern parsed once and reused for every scan.
/// </summary>
public sealed class CompiledPattern
{
    private CompiledPattern(string source, byte[] bytes, byte[] mask, int anchorIndex, int fingerprintStart)
    {
        Source = source;
        Bytes = bytes;
        Mask = mask;
        AnchorIndex = anchorIndex;
        FingerprintStart = fingerprintStart;
    }

    /// <summary>The original pattern string, kept for diagnostics.</summary>
    public string Source { get; }

    /// <summary>Literal byte values; positions covered by a wildcard hold 0.</summary>
    public byte[] Bytes { get; }

    /// <summary>0xFF where <see cref="Bytes"/> must match, 0x00 where it is a wildcard.</summary>
    public byte[] Mask { get; }

    /// <summary>Index into <see cref="Bytes"/> of the literal byte used to seed the search.</summary>
    public int AnchorIndex { get; }

    /// <summary>The literal byte value at <see cref="AnchorIndex"/>.</summary>
    public byte AnchorByte => Bytes[AnchorIndex];

    /// <summary>Pattern length in bytes.</summary>
    public int Length => Bytes.Length;

    /// <summary>
    /// Start of a two-byte literal fingerprint containing <see cref="AnchorIndex"/>, or -1 when
    /// the anchor is isolated by wildcards.
    /// </summary>
    internal int FingerprintStart { get; }

    internal bool HasFingerprint => FingerprintStart >= 0;

    /// <summary>
    /// Parses a pattern of space-separated hex bytes and <c>?</c>/<c>??</c> wildcards,
    /// e.g. <c>"48 89 5C 24 ?? 57 48 83 EC 20"</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The pattern is empty, contains an unparsable token, or consists only of wildcards.
    /// </exception>
    public static CompiledPattern Parse(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException("Pattern is empty.", nameof(pattern));

        string[] parts = pattern.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new ArgumentException($"Pattern '{pattern}' contains no tokens.", nameof(pattern));

        byte[] bytes = new byte[parts.Length];
        byte[] mask = new byte[parts.Length];

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];

            if (part is "?" or "??" or "*")
            {
                bytes[i] = 0x00;
                mask[i] = 0x00;
                continue;
            }

            if (!byte.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
            {
                throw new ArgumentException($"Token '{part}' at index {i} in pattern '{pattern}' is neither a hex byte nor a wildcard.", nameof(pattern));
            }

            bytes[i] = value;
            mask[i] = 0xFF;
        }

        int anchor = SelectAnchor(bytes, mask);
        if (anchor < 0)
            throw new ArgumentException($"Pattern '{pattern}' contains no literal bytes and would match everywhere.", nameof(pattern));

        int fingerprintStart = SelectFingerprint(bytes, mask, anchor);
        return new CompiledPattern(pattern, bytes, mask, anchor, fingerprintStart);
    }

    /// <summary>Tests whether the pattern matches <paramref name="haystack"/> starting at <paramref name="start"/>.</summary>
    public unsafe bool MatchesAt(ReadOnlySpan<byte> haystack, int start)
    {
        if ((uint)start > (uint)haystack.Length || Bytes.Length > haystack.Length - start)
            return false;

        fixed (byte* source = haystack)
            return MatchesAtUnchecked(source + start);
    }

    /// <summary>
    /// Tests an already bounds-checked candidate using masked machine-word loads. Pattern data is
    /// read directly so the publicly exposed byte and mask arrays preserve their existing behavior.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe bool MatchesAtUnchecked(byte* candidate)
    {
        fixed (byte* expected = Bytes)
        fixed (byte* masks = Mask)
        {
            int offset = 0;
            int remaining = Bytes.Length;

            // UInt64 loads are excellent under the x64 JIT. On 32-bit .NET Framework, UInt32
            // avoids turning every comparison into emulated 64-bit operations.
            if (IntPtr.Size == 8)
            {
                while (remaining >= sizeof(UInt64))
                {
                    UInt64 actualWord = Unsafe.ReadUnaligned<UInt64>(candidate + offset);
                    UInt64 expectedWord = Unsafe.ReadUnaligned<UInt64>(expected + offset);
                    UInt64 maskWord = Unsafe.ReadUnaligned<UInt64>(masks + offset);

                    if (((actualWord ^ expectedWord) & maskWord) != 0)
                        return false;

                    offset += sizeof(UInt64);
                    remaining -= sizeof(UInt64);
                }
            }

            while (remaining >= sizeof(UInt32))
            {
                UInt32 actualWord = Unsafe.ReadUnaligned<UInt32>(candidate + offset);
                UInt32 expectedWord = Unsafe.ReadUnaligned<UInt32>(expected + offset);
                UInt32 maskWord = Unsafe.ReadUnaligned<UInt32>(masks + offset);

                if (((actualWord ^ expectedWord) & maskWord) != 0)
                    return false;

                offset += sizeof(UInt32);
                remaining -= sizeof(UInt32);
            }

            while (remaining >= sizeof(UInt16))
            {
                UInt16 actualWord = Unsafe.ReadUnaligned<UInt16>(candidate + offset);
                UInt16 expectedWord = Unsafe.ReadUnaligned<UInt16>(expected + offset);
                UInt16 maskWord = Unsafe.ReadUnaligned<UInt16>(masks + offset);

                if (((actualWord ^ expectedWord) & maskWord) != 0)
                    return false;

                offset += sizeof(UInt16);
                remaining -= sizeof(UInt16);
            }

            if (remaining != 0 && ((candidate[offset] ^ expected[offset]) & masks[offset]) != 0)
                return false;
        }

        return true;
    }

    /// <inheritdoc/>
    public override string ToString() => Source;

    // -------------------------------------------------------------------------
    // Anchor selection
    // -------------------------------------------------------------------------

    /// <summary>
    /// Picks the byte least likely to occur in x86-64 code, so the prefilter fires as rarely as possible.
    /// <para>
    /// Anchoring on the first byte, which is what a naive scan effectively does, is silly: nearly every pattern in the consuming project starts
    /// with 0x48, 0x40, 0x41 or 0x83, all REX/opcode bytes that occur every few bytes in a real module. 
    /// Anchoring on, say, the 0xB7 or 0x69 further into the pattern cuts verification attempts by two to three orders of magnitude.
    /// </para>
    /// </summary>
    private static int SelectAnchor(byte[] bytes, byte[] mask)
    {
        int best = -1;
        int bestScore = int.MaxValue;

        for (int i = 0; i < bytes.Length; i++)
        {
            if (mask[i] == 0)
                continue;

            int score = Frequency[bytes[i]];
            if (score < bestScore)
            {
                bestScore = score;
                best = i;

                if (score == 1)
                    break; // Cannot do better than "rare".
            }
        }

        return best;
    }

    /// <summary>
    /// Extends the selected rare byte with the rarer adjacent literal when possible. The SIMD seed
    /// remains the same byte; the neighbour rejects false candidates before a pattern bucket is
    /// visited.
    /// </summary>
    private static int SelectFingerprint(byte[] bytes, byte[] mask, int anchor)
    {
        int bestStart = -1;
        int bestNeighbourScore = int.MaxValue;

        if (anchor > 0 && mask[anchor - 1] != 0)
        {
            bestStart = anchor - 1;
            bestNeighbourScore = Frequency[bytes[anchor - 1]];
        }

        if (anchor + 1 < bytes.Length && mask[anchor + 1] != 0)
        {
            int score = Frequency[bytes[anchor + 1]];
            if (score < bestNeighbourScore)
                bestStart = anchor;
        }

        return bestStart;
    }

    /// <summary>
    /// Coarse relative frequency of each byte value in compiled x86-64 code. 
    /// Exact values do not matter, only the ordering: high means "appears constantly, do not anchor here".
    /// </summary>
    private static readonly int[] Frequency = BuildFrequencyTable();

    private static int[] BuildFrequencyTable()
    {
        int[] table = new int[256];
        for (int i = 0; i < table.Length; i++)
            table[i] = 1;

        // Padding, zeroed operands and immediates.
        table[0x00] = 1000;
        table[0xCC] = 400;
        table[0x90] = 200;
        table[0xFF] = 300;

        // REX prefixes.
        table[0x48] = 900; table[0x49] = 250; table[0x4C] = 250; table[0x4D] = 120;
        table[0x40] = 200; table[0x41] = 400; table[0x44] = 250; table[0x45] = 120;

        // Extremely common opcodes / ModRM / SIB bytes.
        table[0x8B] = 700; table[0x89] = 600; table[0x8D] = 300;
        table[0xE8] = 350; table[0xE9] = 150; table[0xEB] = 150;
        table[0x0F] = 400; table[0x84] = 300; table[0x85] = 300;
        table[0x74] = 250; table[0x75] = 250; table[0xC3] = 250;
        table[0x83] = 300; table[0x81] = 150; table[0x39] = 120;
        table[0x24] = 500; table[0x25] = 150; table[0x20] = 250;
        table[0x5C] = 200; table[0x54] = 200; table[0x4B] = 120;
        table[0x10] = 200; table[0x08] = 200; table[0x18] = 150;
        table[0x28] = 150; table[0x30] = 150; table[0x38] = 150;
        table[0x01] = 200; table[0x02] = 180; table[0x04] = 180;
        table[0xC4] = 150; table[0xEC] = 150; table[0x56] = 150;
        table[0x57] = 150; table[0x5F] = 150; table[0x5E] = 150;
        table[0x5B] = 150; table[0x5D] = 150; table[0xC7] = 150;
        table[0x63] = 120; table[0xB8] = 120;

        return table;
    }
}
