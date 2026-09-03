using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RedBird.Core.Memory;

namespace RedBird.Core.Platform;

/// <summary>
/// Reads the current process's address space from <c>/proc/self/maps</c>.
/// </summary>
public static class ProcMaps
{
    private const string MapsPath = "/proc/self/maps";

    /// <summary>True when this platform exposes the map.</summary>
    public static bool IsAvailable => PlatformInfo.HasProcMaps && File.Exists(MapsPath);

    /// <summary>Enumerates every mapped range, in ascending address order.</summary>
    public static List<MappedRegion> Read()
    {
        List<MappedRegion> regions = [];

        if (!IsAvailable)
            return regions;

        // A plain File.ReadAllLines works, but /proc entries report zero length and can change between reads; streaming keeps the snapshot as tight as possible.
        using FileStream stream = new(MapsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (TryParseLine(line, out MappedRegion region))
                regions.Add(region);
        }

        return regions;
    }

    /// <summary>Finds the range containing <paramref name="address"/>.</summary>
    public static bool TryFindRegion(UInt64 address, out MappedRegion region)
    {
        region = default;

        if (!IsAvailable)
            return false;

        using FileStream stream = new(MapsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!TryParseLine(line, out MappedRegion candidate))
                continue;

            if (address >= candidate.Start && address < candidate.End)
            {
                region = candidate;
                return true;
            }

            // Entries are ordered, so once we are past the address there is nothing left to find.
            if (candidate.Start > address)
                break;
        }

        return false;
    }

    /// <summary>Returns the file backing the mapping at <paramref name="address"/>, if any.</summary>
    public static bool TryGetModulePath(UInt64 address, out string path)
    {
        path = string.Empty;

        if (!TryFindRegion(address, out MappedRegion region) || string.IsNullOrEmpty(region.Path))
            return false;

        path = region.Path!;
        return true;
    }

    /// <summary>
    /// Finds an unmapped hole of at least <paramref name="size"/> bytes inside <c>[low, high)</c>, aligned to <paramref name="alignment"/>.
    /// </summary>
    /// <remarks>
    /// Scans forward from <paramref name="low"/>, preferring the lowest suitable hole. 
    /// The result is advisory, another thread can map into it before the caller does, so the caller must still verify what <c>mmap</c> actually returned.
    /// </remarks>
    public static bool TryFindFreeGap(UInt64 low, UInt64 high, UInt64 size, UInt64 alignment, out UInt64 address)
    {
        address = 0;

        if (size == 0 || low >= high)
            return false;

        List<MappedRegion> regions = Read();
        if (regions.Count == 0)
            return false;

        UInt64 cursor = AlignUp(low, alignment);

        foreach (MappedRegion region in regions)
        {
            if (region.End <= cursor)
                continue;

            if (region.Start > cursor)
            {
                UInt64 gapEnd = Math.Min(region.Start, high);
                if (gapEnd > cursor && gapEnd - cursor >= size)
                {
                    address = cursor;
                    return true;
                }
            }

            cursor = AlignUp(Math.Max(cursor, region.End), alignment);

            if (cursor >= high)
                return false;
        }

        // Past the last mapping, still inside the window.
        if (cursor < high && high - cursor >= size)
        {
            address = cursor;
            return true;
        }

        return false;
    }

    private static UInt64 AlignUp(UInt64 value, UInt64 alignment)
        => alignment <= 1 ? value : (value + alignment - 1) & ~(alignment - 1);

    /// <summary>
    /// Parses one line, e.g.
    /// <c>7f8e0c000000-7f8e0c021000 r-xp 00000000 08:01 1234 /usr/lib/libc.so.6</c>.
    /// </summary>
    private static bool TryParseLine(string line, out MappedRegion region)
    {
        region = default;

        int dash = line.IndexOf('-');
        if (dash <= 0)
            return false;

        int firstSpace = line.IndexOf(' ', dash);
        if (firstSpace < 0)
            return false;

#if NET8_0_OR_GREATER
        if (!UInt64.TryParse(line.AsSpan(0, dash), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out UInt64 start))
            return false;
        if (!UInt64.TryParse(line.AsSpan(dash + 1, firstSpace - dash - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out UInt64 end))
            return false;
#else
        if (!UInt64.TryParse(line.Substring(0, dash), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out UInt64 start))
            return false;
        if (!UInt64.TryParse(line.Substring(dash + 1, firstSpace - dash - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out UInt64 end))
            return false;
#endif

        int permsStart = firstSpace + 1;
        if (permsStart + 4 > line.Length)
            return false;

        MemoryProtection protection = MemoryProtection.NoAccess;
        if (line[permsStart] == 'r')
            protection |= MemoryProtection.Read;
        if (line[permsStart + 1] == 'w')
            protection |= MemoryProtection.Write;
        if (line[permsStart + 2] == 'x')
            protection |= MemoryProtection.Execute;

        bool isPrivate = line[permsStart + 3] == 'p';

        // Path, when present, is the last field and starts after the inode column. Anonymous mappings have no path; pseudo-paths such as [heap] or [stack] are kept as-is.
        string? path = null;
        int pathStart = line.IndexOf('/', permsStart);
        int bracketStart = line.IndexOf('[', permsStart);

        if (pathStart > 0 && (bracketStart < 0 || pathStart < bracketStart))
#if NET8_0_OR_GREATER
            path = line[pathStart..].Trim();
#else
            path = line.Substring(pathStart).Trim();
#endif
        else if (bracketStart > 0)
#if NET8_0_OR_GREATER
            path = line[bracketStart..].Trim();
#else
            path = line.Substring(bracketStart).Trim();
#endif

        region = new MappedRegion(start, end, protection, isPrivate, path);
        return true;
    }
}
