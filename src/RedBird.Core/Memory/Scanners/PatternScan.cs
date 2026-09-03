using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RedBird.Core.Memory.Scanners;

/// <summary>
/// Original source: https://github.com/mrexodia/PatternFinder/blob/master/PatternFinder/Pattern.cs
/// Naive / Lineaer search with rewind.
/// </summary>
public static class PatternScanner
{
    /// <summary>
    /// Finds the address of a pattern within a memory region using a specified pattern and mask.
    /// This implementation is refactored to use ReadOnlySpan for performance.
    /// </summary>
    /// <param name="span">The memory region to scan.</param>
    /// <param name="pattern">The byte pattern to look for.</param>
    /// <param name="mask">A string that determines how the pattern should be matched. 'x' requires a match, while '?' acts as a wildcard.</param>
    /// <returns>The zero-based index of the first occurrence of the pattern, or -1 if not found.</returns>
    public static long FindPattern(ReadOnlySpan<byte> span, ReadOnlySpan<byte> pattern, string mask)
    {
        long spanLength = span.Length;
        int patternLength = pattern.Length;

        // Basic validation: ensure the memory span is large enough and the mask matches the pattern length.
        if (spanLength < patternLength || patternLength != mask.Length)
        {
            return -1;
        }

        int patternPosition = 0;
        for (long spanIndex = 0; spanIndex < spanLength; spanIndex++)
        {
            // Check if the current byte in the span matches the pattern byte, or if the mask is a wildcard.
            if (mask[patternPosition] == '?' || span[(int)spanIndex] == pattern[patternPosition])
            {
                patternPosition++;
                // If we have matched the entire pattern, return the starting index.
                if (patternPosition == patternLength)
                {
                    return spanIndex - patternLength + 1;
                }
            }
            else
            {
                // On mismatch, rewind the span index to the byte after the start of the last partial match,
                // and reset the pattern position. This is the core of the original algorithm.
                spanIndex -= patternPosition;
                patternPosition = 0;
            }
        }

        return -1;
    }

    /// <summary>
    /// Finds the address of a pattern within a memory region using a string-based pattern.
    /// The pattern string can contain bytes in hexadecimal format (e.g., "4A", "80") and wildcards ("?" or "??").
    /// Example pattern: "48 89 5C 24 ? 57 48 83 EC 20"
    /// </summary>
    /// <param name="span">The memory region to scan.</param>
    /// <param name="patternString">A string representing the pattern, with hex bytes and '?' as wildcards, separated by spaces.</param>
    /// <returns>The zero-based index of the first occurrence of the pattern, or -1 if not found.</returns>
    public static long FindPattern(ReadOnlySpan<byte> span, string patternString)
    {
        (byte[]? pattern, string? mask) = ParsePatternString(patternString);

        if (pattern.Length == 0)
        {
            return -1;
        }

        // Call the core FindPattern function with the generated pattern and mask.
        return FindPattern(span, pattern, mask);
    }

    /// <summary>
    /// Parses a string pattern into a byte array and a mask string.
    /// </summary>
    private static (byte[] pattern, string mask) ParsePatternString(string patternString)
    {
        // Split the pattern string by spaces, removing any empty entries that result from multiple spaces.
        string[] parts = patternString.Split([' '], StringSplitOptions.RemoveEmptyEntries);
        List<byte> patternBytes = new(parts.Length);
        StringBuilder maskBuilder = new(parts.Length);

        foreach (string part in parts)
        {
            // If the part is a wildcard, add a placeholder byte (0x00) and a '?' to the mask.
            if (part == "?" || part == "??")
            {
                patternBytes.Add(0x00); // Placeholder byte, won't be checked.
                maskBuilder.Append('?');
            }
            // Otherwise, attempt to parse the hex string to a byte.
            else if (byte.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte result))
            {
                patternBytes.Add(result);
                maskBuilder.Append('x');
            }
            // Note: Invalid parts in the pattern string are currently ignored.
        }

        return (patternBytes.ToArray(), maskBuilder.ToString());
    }
}
