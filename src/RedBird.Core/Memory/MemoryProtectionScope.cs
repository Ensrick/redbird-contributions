using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RedBird.Core.PInvoke;
using RedBird.Core.Platform;

namespace RedBird.Core.Memory;

/// <summary>
/// One contiguous part of a protected range with a single native protection value.
/// </summary>
/// <remarks>
/// The protection is deliberately kept in its native representation. Converting a Win32 protection through <see cref="MemoryProtection"/> would discard modifiers such as  <c>PAGE_GUARD</c> and <c>PAGE_NOCACHE</c> before the range is restored.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct MemoryProtectionRegion
{
    public UInt64 Address;
    public UInt64 Length;
    public UInt32 NativeProtection;
}

/// <summary>
/// Temporarily changes every page touched by a range and restores each original protection region independently.
/// </summary>
/// <remarks>
/// Win32 <c>VirtualProtect</c> only returns the previous protection of the first page. POSIX <c>mprotect</c> returns no previous protection at all. 
/// A single saved value is therefore not sufficient when a patch crosses a boundary between differently protected pages.
/// </remarks>
internal sealed unsafe class MemoryProtectionScope : IDisposable
{
    private readonly MemoryProtectionRegion[] _regions;
    private bool _disposed;

    private MemoryProtectionScope(MemoryProtectionRegion[] regions)
    {
        _regions = regions;
    }

    /// <summary>
    /// Captures the complete affected page range and changes every captured region to <paramref name="temporaryProtection"/>. Any partial change is rolled back before failure is reported.
    /// </summary>
    internal static bool TryAcquire(IntPtr address, int length, MemoryProtection temporaryProtection, out MemoryProtectionScope? scope)
    {
        scope = null;
        MemoryProtectionRegion[] regions = CaptureRegions(address, length);

        // Do not let a writable window silently rewrite Control Flow Guard target metadata.
        UInt32 nativeTemporaryProtection = PlatformInfo.IsWindows ? MemoryProtectionConverter.ToWin32(temporaryProtection | MemoryProtection.ControlFlowGuard) : unchecked((UInt32)MemoryProtectionConverter.ToPosix(temporaryProtection));

        int changedCount = 0;
        for (; changedCount < regions.Length; changedCount++)
        {
            if (ApplyTemporary(in regions[changedCount], nativeTemporaryProtection))
                continue;

            if (!Restore(regions, changedCount))
            {
                throw new InvalidOperationException($"Could not roll back a partial memory-protection change at 0x{(UInt64)address:X16}.");
            }

            return false;
        }

        scope = new MemoryProtectionScope(regions);
        return true;
    }

    /// <summary>
    /// Captures raw Win32 protection regions for the thread-suspending patcher. The caller changes and restores these records inside its allocation-free critical section.
    /// </summary>
    internal static MemoryProtectionRegion[] CaptureWindowsRegions(UInt64 address, int length)
    {
        GetPageRange(address, length, out UInt64 pageStart, out UInt64 pageEnd);
        List<MemoryProtectionRegion> regions = [];
        UInt64 current = pageStart;
        UInt32 infoSize = checked((UInt32)Marshal.SizeOf<NativeMethods.Win32MEMORY_BASIC_INFORMATION>());

        while (current < pageEnd)
        {
            if (WindowsMemoryMethods.VirtualQuery((IntPtr)current, out NativeMethods.Win32MEMORY_BASIC_INFORMATION info, (UIntPtr)infoSize) == UIntPtr.Zero)
            {
                throw new InvalidOperationException($"VirtualQuery failed for 0x{current:X16}. Win32 error: {Marshal.GetLastWin32Error()}.");
            }

            UInt64 regionStart = (UInt64)info.BaseAddress;
            UInt64 regionSize = unchecked((UInt64)info.RegionSize.ToInt64());
            UInt64 regionEnd = checked(regionStart + regionSize);
            if (regionSize == 0 || regionEnd <= current || info.State != 0x1000)
                throw new InvalidOperationException($"VirtualQuery returned an invalid region for 0x{current:X16}.");

            UInt64 segmentEnd = Math.Min(pageEnd, regionEnd);
            regions.Add(new MemoryProtectionRegion
            {
                Address = current,
                Length = segmentEnd - current,
                NativeProtection = info.Protect,
            });

            current = segmentEnd;
        }

        return [.. regions];
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        bool restored = Restore(_regions, _regions.Length);
        _disposed = true;

        if (!restored)
            throw new InvalidOperationException("Could not restore one or more original memory-protection regions.");
    }

    internal static MemoryProtectionRegion[] CaptureRegions(IntPtr address, int length)
    {
        if (address == IntPtr.Zero)
            throw new ArgumentNullException(nameof(address));

        UInt64 start = (UInt64)address;
        if (PlatformInfo.IsWindows)
            return CaptureWindowsRegions(start, length);

        GetPageRange(start, length, out UInt64 pageStart, out UInt64 pageEnd);

        // Linux exposes exact mapping boundaries and r/w/x flags through /proc/self/maps.
        if (ProcMaps.IsAvailable)
        {
            List<MemoryProtectionRegion> regions = [];
            List<MappedRegion> mappings = ProcMaps.Read();
            UInt64 current = pageStart;

            foreach (MappedRegion mapping in mappings)
            {
                if (mapping.End <= current)
                    continue;
                if (mapping.Start > current)
                    break;

                UInt64 segmentEnd = Math.Min(pageEnd, mapping.End);
                regions.Add(new MemoryProtectionRegion
                {
                    Address = current,
                    Length = segmentEnd - current,
                    NativeProtection = unchecked((UInt32)MemoryProtectionConverter.ToPosix(mapping.Protection)),
                });

                current = segmentEnd;
                if (current == pageEnd)
                    return [.. regions];
            }

            throw new InvalidOperationException($"The complete protection range 0x{pageStart:X16}-0x{pageEnd:X16} is not mapped.");
        }

        // macOS and the BSDs do not expose /proc/self/maps. Retain the library's documented, caller-configurable fallback rather than pretending mprotect reported an old value.
        return
        [
            new MemoryProtectionRegion
            {
                Address = pageStart,
                Length = pageEnd - pageStart,
                NativeProtection = unchecked((UInt32)MemoryProtectionConverter.ToPosix(NativeMemoryManager.AssumedProtection)),
            },
        ];
    }

    private static bool Apply(in MemoryProtectionRegion region, UInt32 nativeProtection)
    {
        if (PlatformInfo.IsWindows)
        {
            return WindowsMemoryMethods.VirtualProtect((IntPtr)region.Address, checked((UIntPtr)region.Length), (NativeMethods.Win32MemoryPermissions)nativeProtection, out _);
        }

        return PosixNativeMethods.Protect((IntPtr)region.Address, (UIntPtr)region.Length, unchecked((Int32)nativeProtection));
    }

    private static bool ApplyTemporary(in MemoryProtectionRegion region, UInt32 nativeProtection)
    {
        if (Apply(in region, nativeProtection))
            return true;

        // PAGE_TARGETS_NO_UPDATE was added with CFG. An older Windows kernel can reject it even though the underlying RWX transition is valid; such a kernel has no CFG state to lose.
        const UInt32 PageTargetsNoUpdate = 0x40000000;
        return PlatformInfo.IsWindows && (nativeProtection & PageTargetsNoUpdate) != 0 && Apply(in region, nativeProtection & ~PageTargetsNoUpdate);
    }

    private static bool ApplyRestore(in MemoryProtectionRegion region)
    {
        if (!PlatformInfo.IsWindows)
            return Apply(in region, region.NativeProtection);

        // Preserve the page's CFG target bitmap while restoring its access flags. Retry without the modifier only for pre-CFG Windows kernels.
        const UInt32 PageTargetsNoUpdate = 0x40000000;
        bool wasExecutable = (region.NativeProtection & 0xF0) != 0;
        UInt32 restoreProtection = wasExecutable ? region.NativeProtection | PageTargetsNoUpdate : region.NativeProtection;
        return Apply(in region, restoreProtection) || (restoreProtection != region.NativeProtection && Apply(in region, region.NativeProtection));
    }

    private static bool Restore(MemoryProtectionRegion[] regions, int count)
    {
        bool success = true;
        for (int i = count - 1; i >= 0; i--)
        {
            if (!ApplyRestore(in regions[i]))
                success = false;
        }

        return success;
    }

    private static void GetPageRange(UInt64 address, int length, out UInt64 pageStart, out UInt64 pageEnd)
    {
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        UInt64 pageMask = checked((UInt64)PlatformInfo.PageSize - 1);
        pageStart = address & ~pageMask;
        UInt64 byteEnd = checked(address + (UInt64)length);
        pageEnd = checked(byteEnd + pageMask) & ~pageMask;
    }
}
