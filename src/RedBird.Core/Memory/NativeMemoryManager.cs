using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RedBird.Core.PInvoke;
using RedBird.Core.Platform;

namespace RedBird.Core.Memory;

/// <summary>
/// Allocation, protection and proximity-constrained stub placement, on Windows and POSIX.
/// </summary>
public static unsafe class NativeMemoryManager
{
    private const int SlabSize = 64 * 1024;
    private const int SlabAlignment = 16;
    private const UInt64 RipReach = 0x7000_0000UL;

    /// <summary>How many candidate holes to try before giving up on a proximity allocation.</summary>
    private const int MaxPlacementAttempts = 32;

    private static readonly object _slabLock = new();
    private static readonly List<StubSlab> _slabs = [];

    private sealed class StubSlab(IntPtr baseAddress, bool isWriteExecute)
    {
        public IntPtr BaseAddress { get; } = baseAddress;
        public bool IsWriteExecute { get; } = isWriteExecute;
        public int Offset { get; set; }

        public bool HasSpace(int size) => Offset + size <= SlabSize;

        public bool Reaches(UInt64 targetAddress)
        {
            UInt64 slabAddress = (UInt64)BaseAddress;
            UInt64 delta = slabAddress > targetAddress ? slabAddress - targetAddress : targetAddress - slabAddress;
            return delta < RipReach;
        }

        public bool Contains(IntPtr address, int length)
        {
            UInt64 start = (UInt64)address;
            UInt64 slabStart = (UInt64)BaseAddress;
            UInt64 slabEnd = slabStart + SlabSize;
            UInt64 end = start + (UInt64)length;
            return start >= slabStart && end >= start && end <= slabEnd;
        }
    }

    /// <summary>
    /// True when the platform can report a range's current protection. False on macOS and the BSDs, where <see cref="Protect"/> falls back to <see cref="AssumedProtection"/> for the previous value.
    /// </summary>
    public static bool CanQueryProtection => PlatformInfo.IsWindows || ProcMaps.IsAvailable;

    /// <summary>
    /// Previous protection reported when the platform cannot be queried. 
    /// Defaults to <see cref="MemoryProtection.ExecuteRead"/>, which is what a code page normally is the case this library patches. Set it if you are patching data pages on such a platform.
    /// </summary>
    public static MemoryProtection AssumedProtection { get; set; } = MemoryProtection.ExecuteRead;

    // -------------------------------------------------------------------------
    // Stub allocation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Carves an executable stub out of a slab placed within +/-1.75 GB of <paramref name="targetAddress"/>, so RIP-relative operands and rel32 branches stay in range.
    /// </summary>
    public static IntPtr AllocateStub(UInt64 targetAddress, int size)
        => AllocateStubCore(targetAddress, size, requireNear: true);

    /// <summary>
    /// Carves an executable stub out of an unconstrained slab. Intended for x86-32, where a rel32 branch can reach the entire 32-bit virtual address space and proximity has no value.
    /// </summary>
    public static IntPtr AllocateStub(int size)
        => AllocateStubCore(0, size, requireNear: false);

    private static IntPtr AllocateStubCore(UInt64 targetAddress, int size, bool requireNear)
    {
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "A stub must contain at least one byte.");

        int alignedSize = (int)(((UInt64)size + SlabAlignment - 1) & ~(UInt64)(SlabAlignment - 1));

        if (alignedSize > SlabSize)
            throw new ArgumentOutOfRangeException(nameof(size), $"A single stub cannot exceed the {SlabSize} byte slab size.");

        lock (_slabLock)
        {
            // Search every live slab, not just the most recently allocated one.
            // Keeping only one slab's metadata made old slabs impossible to identify in WriteStub/FinalizeStub and let a page-level protection change corrupt unrelated live hooks.
            for (int i = _slabs.Count - 1; i >= 0; i--)
            {
                StubSlab slab = _slabs[i];
                if (!slab.HasSpace(alignedSize) || (requireNear && !slab.Reaches(targetAddress)))
                    continue;

                IntPtr existing = slab.BaseAddress + slab.Offset;
                slab.Offset += alignedSize;
                return existing;
            }

            IntPtr slabAddress;
            if (requireNear)
            {
                UInt64 low = targetAddress > RipReach ? targetAddress - RipReach : 0;
                UInt64 high = targetAddress > UInt64.MaxValue - RipReach
                    ? UInt64.MaxValue
                    : targetAddress + RipReach;
                slabAddress = (IntPtr)BoundAlloc(low, high, SlabSize, MemoryProtection.ExecuteReadWrite);
            }
            else
            {
                slabAddress = Alloc(SlabSize, MemoryProtection.ExecuteReadWrite);
            }

            if (slabAddress == IntPtr.Zero)
                return IntPtr.Zero;

            // Find out once, for the whole slab, whether this host tolerates write+execute.
            // The allocators ask for it, but a W^X policy can make PosixMap fall back to read-write.
            bool isWriteExecute = Protect(slabAddress, SlabSize, MemoryProtection.ExecuteReadWrite, out _);

            if (!isWriteExecute && !Protect(slabAddress, SlabSize, MemoryProtection.ExecuteRead, out _))
            {
                Free(slabAddress, SlabSize);
                return IntPtr.Zero;
            }

            StubSlab created = new(slabAddress, isWriteExecute) { Offset = alignedSize };
            _slabs.Add(created);
            return slabAddress;
        }
    }

    /// <summary>
    /// Copies code or data into a stub obtained from <see cref="AllocateStub(UInt64, int)"/> or <see cref="AllocateStub(int)"/>, taking care of write permission if the host refuses write+execute mappings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Use this rather than writing through the pointer directly.</b> On a W^X host the slab is mapped execute-read and a direct store faults; this grants write access for the duration of the copy and takes it away again.
    /// </para>
    /// <para>
    /// During that window other stubs sharing the same page are temporarily non-executable, which is unavoidable  protection is page-granular and stubs are sub-page allocations. 
    /// It is also the reason write+execute is preferred where the OS still allows it: there, this is a plain memory copy.
    /// </para>
    /// </remarks>
    public static bool WriteStub(IntPtr destination, ReadOnlySpan<byte> source)
    {
        if (destination == IntPtr.Zero || source.IsEmpty)
            return false;

        lock (_slabLock)
        {
            StubSlab? slab = null;
            foreach (StubSlab candidate in _slabs)
            {
                if (candidate.Contains(destination, source.Length))
                {
                    slab = candidate;
                    break;
                }
            }

            if (slab is null)
                return false;

            bool needsWriteWindow = !slab.IsWriteExecute;
            if (needsWriteWindow && !Protect(destination, source.Length, MemoryProtection.ReadWrite, out _))
                return false;

            try
            {
                fixed (byte* bytes = source)
                {
                    Buffer.MemoryCopy(bytes, (void*)destination, source.Length, source.Length);
                }
            }
            finally
            {
                if (needsWriteWindow)
                    Protect(destination, source.Length, MemoryProtection.ExecuteRead, out _);
            }

            InstructionCache.Flush((UInt64)destination, source.Length);
            return true;
        }
    }

    /// <summary>Writes a single value into a stub slot. Used for indirect-jump destinations.</summary>
    public static bool WriteStub<T>(IntPtr destination, T value) where T : unmanaged
        => WriteStub(destination, new ReadOnlySpan<byte>(&value, sizeof(T)));

    /// <summary>
    /// Flips a standalone executable allocation from writable to executable-read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never call this on a result of <see cref="AllocateStub(UInt64, int)"/> or <see cref="AllocateStub(int)"/>.</b> 
    /// Stubs are bump allocations inside a shared slab and protection is page-granular, so taking write access away from one stub takes it away from every other stub on the same page, including the
    /// ones handed out afterwards, which then fault on their first store. Use <see cref="WriteStub(IntPtr, ReadOnlySpan{byte})"/> instead, which knows about the slab.
    /// </para>
    /// </remarks>
    public static bool FinalizeStub(IntPtr address, int length)
    {
        lock (_slabLock)
        {
            foreach (StubSlab slab in _slabs)
            {
                if (slab.Contains(address, Math.Max(length, 1)))
                {
                    throw new InvalidOperationException($"FinalizeStub was called on 0x{(UInt64)address:X16}, which is inside a shared stub slab. That would revoke write access from every other stub on the same page, including ones not yet handed out. Use WriteStub to write stub contents instead.");
                }
            }
        }

        return Protect(address, length, MemoryProtection.ExecuteRead, out _);
    }

    // -------------------------------------------------------------------------
    // Allocation
    // -------------------------------------------------------------------------

    /// <summary>Allocates <paramref name="size"/> bytes anywhere.</summary>
    public static IntPtr Alloc(int size, MemoryProtection protection = MemoryProtection.ExecuteReadWrite)
    {
        if (PlatformInfo.IsWindows)
        {
            return NativeMethods.VirtualAlloc(IntPtr.Zero, (UIntPtr)size, NativeMethods.Win32AllocationType.MEM_COMMIT | NativeMethods.Win32AllocationType.MEM_RESERVE, (NativeMethods.Win32MemoryPermissions)MemoryProtectionConverter.ToWin32(protection));
        }

        return PosixMap(IntPtr.Zero, (UInt64)size, protection, fixedNoReplace: false);
    }

    /// <summary>Releases a whole allocation.</summary>
    /// <remarks>
    /// Only valid for the base address of an allocation. Sub-allocations carved out of a slab by <see cref="AllocateStub(UInt64, int)"/> and <see cref="AllocateStub(int)"/> must never be
    /// passed here: on Windows <c>MEM_RELEASE</c> requires the base of the reservation, and releasing the slab base unmaps every other stub living in it.
    /// </remarks>
    public static bool Free(IntPtr address, int size)
    {
        if (PlatformInfo.IsWindows)
            return NativeMethods.VirtualFree(address, UIntPtr.Zero, (uint)NativeMethods.Win32AllocationType.MEM_RELEASE);

        return PosixNativeMethods.Unmap(address, (UIntPtr)size);
    }

    /// <summary>
    /// Changes protection over <paramref name="size"/> bytes at <paramref name="address"/>.
    /// </summary>
    /// <param name="address">The base address of the memory region to protect.</param>
    /// <param name="size">The size of the memory region to protect.</param>
    /// <param name="oldProtection">The previous protection, for restoring later. 
    /// On platforms where <see cref="CanQueryProtection"/> is false this is <see cref="AssumedProtection"/> rather than a real reading. 
    /// The call fails without changing anything when the affected pages do not all have the same original protection; one return value could not restore such a range safely. </param>
    /// <param name="newProtection">The new protection to apply. On POSIX this is a hint only.</param>
    public static bool Protect(IntPtr address, int size, MemoryProtection newProtection, out MemoryProtection oldProtection)
    {
        oldProtection = MemoryProtection.NoAccess;

        if (size <= 0)
            return false;

        MemoryProtectionRegion[] regions;
        try
        {
            regions = MemoryProtectionScope.CaptureRegions(address, size);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }

        UInt32 nativeOldProtection = regions[0].NativeProtection;
        for (int i = 1; i < regions.Length; i++)
        {
            if (regions[i].NativeProtection != nativeOldProtection)
                return false;
        }

        if (PlatformInfo.IsWindows)
        {
            NativeMethods.Win32MemoryPermissions previous;
            bool ok = NativeMethods.VirtualProtect(address, (uint)size, (NativeMethods.Win32MemoryPermissions)MemoryProtectionConverter.ToWin32(newProtection), &previous);
            oldProtection = MemoryProtectionConverter.FromWin32((uint)previous);
            return ok;
        }

        UInt64 target = (UInt64)address;
        oldProtection = MemoryProtectionConverter.FromPosix(unchecked((Int32)nativeOldProtection));

        // mprotect requires a page-aligned address and applies to whole pages; passing the raw address and length fails with EINVAL.
        IntPtr pageStart = (IntPtr)PlatformInfo.AlignDownToPage(target);
        UIntPtr span = PlatformInfo.PageSpan(target, size);

        return PosixNativeMethods.Protect(pageStart, span, MemoryProtectionConverter.ToPosix(newProtection));
    }

    /// <summary>Reports the current protection of the page containing <paramref name="address"/>.</summary>
    public static MemoryProtection QueryProtection(UInt64 address)
    {
        if (PlatformInfo.IsWindows)
        {
            NativeMethods.Win32MEMORY_BASIC_INFORMATION info = default;
            if (NativeMethods.VirtualQueryEx(InstructionCache.CurrentProcessHandle, (IntPtr)address, &info, (uint)Marshal.SizeOf<NativeMethods.Win32MEMORY_BASIC_INFORMATION>()) != 0)
            {
                return MemoryProtectionConverter.FromWin32(info.Protect);
            }

            return AssumedProtection;
        }

        if (ProcMaps.TryFindRegion(address, out MappedRegion region))
            return region.Protection;

        return AssumedProtection;
    }

    // -------------------------------------------------------------------------
    // Cross-process helpers
    // -------------------------------------------------------------------------

    /// <summary>Writes a value, in-process on POSIX.</summary>
    public static bool Write<T>(IntPtr processHandle, IntPtr address, T data) where T : unmanaged
    {
        uint size = (uint)Unsafe.SizeOf<T>();

        if (PlatformInfo.IsWindows)
        {
            uint written = 0;
            return NativeMethods.WriteProcessMemory(processHandle, address, (byte*)&data, size, &written);
        }

        // POSIX has no portable cross-process write; process_vm_writev is Linux-only and does not bypass page protection anyway. In-process is the only supported case here.
        Unsafe.CopyBlock((void*)address, &data, size);
        return true;
    }

    /// <summary>Reads a value, in-process on POSIX.</summary>
    public static bool Read<T>(IntPtr processHandle, IntPtr address, out T data) where T : unmanaged
    {
        data = default;
        uint size = (uint)Unsafe.SizeOf<T>();

        fixed (T* destination = &data)
        {
            if (PlatformInfo.IsWindows)
            {
                uint read = 0;
                return NativeMethods.ReadProcessMemory(processHandle, address, (byte*)destination, size, &read);
            }

            Unsafe.CopyBlock(destination, (void*)address, size);
            return true;
        }
    }

    // -------------------------------------------------------------------------
    // Proximity allocation
    // -------------------------------------------------------------------------

    private static UInt64 BoundAlloc(UInt64 low, UInt64 high, UInt64 size, MemoryProtection protection)
        => PlatformInfo.IsWindows
            ? WindowsBoundAlloc(low, high, size, protection)
            : PosixBoundAlloc(low, high, size, protection);

    /// <summary>
    /// Places a mapping inside <c>[low, high)</c> on POSIX.
    /// <para>
    /// <c>mmap</c>'s address argument is only a hint the kernel may ignore, so this walks <c>/proc/self/maps</c> for a hole and asks for it with <c>MAP_FIXED_NOREPLACE</c>, which
    /// fails rather than relocating if the hole was taken in the meantime. Where the map is not readable (macOS) it degrades to hint-and-verify, discarding anything that lands outside the window.
    /// </para>
    /// </summary>
    private static UInt64 PosixBoundAlloc(UInt64 low, UInt64 high, UInt64 size, MemoryProtection protection)
    {
        UInt64 pageSize = (UInt64)PlatformInfo.PageSize;
        low = (low + pageSize - 1) & ~(pageSize - 1);
        high &= ~(pageSize - 1);

        if (low >= high || high - low < size)
            return 0;

        if (ProcMaps.IsAvailable)
        {
            UInt64 cursor = low;

            for (int attempt = 0; attempt < MaxPlacementAttempts; attempt++)
            {
                if (!ProcMaps.TryFindFreeGap(cursor, high, size, pageSize, out UInt64 candidate))
                    break;

                IntPtr result = PosixMap((IntPtr)candidate, size, protection, fixedNoReplace: true);
                if (result != IntPtr.Zero && IsWithin((UInt64)result, low, high, size))
                    return (UInt64)result;

                // Either the hole was taken between the read and the map, or an older kernel ignored MAP_FIXED_NOREPLACE and relocated us out of the window.
                if (result != IntPtr.Zero)
                    PosixNativeMethods.Unmap(result, (UIntPtr)size);

                cursor = candidate + pageSize;
                if (cursor >= high)
                    break;
            }

            return 0;
        }

        IntPtr hinted = PosixMap((IntPtr)(low + (high - low) / 2), size, protection, fixedNoReplace: false);
        if (hinted == IntPtr.Zero)
            return 0;

        if (IsWithin((UInt64)hinted, low, high, size))
            return (UInt64)hinted;

        PosixNativeMethods.Unmap(hinted, (UIntPtr)size);
        return 0;
    }

    private static bool IsWithin(UInt64 address, UInt64 low, UInt64 high, UInt64 size)
        => address >= low && address <= high && size <= high - address;

    /// <summary>
    /// Wraps <c>mmap</c>: normalises <c>MAP_FAILED</c> to <see cref="IntPtr.Zero"/> and falls back to a write-only-then-finalise mapping where the kernel refuses W+X.
    /// </summary>
    private static IntPtr PosixMap(IntPtr address, UInt64 size, MemoryProtection protection, bool fixedNoReplace)
    {
        int flags = PosixNativeMethods.MAP_PRIVATE | PosixNativeMethods.MAP_ANONYMOUS;

        if (fixedNoReplace && PlatformInfo.IsLinux)
            flags |= PosixNativeMethods.MAP_FIXED_NOREPLACE;

        IntPtr result = PosixNativeMethods.Map(address, (UIntPtr)size, MemoryProtectionConverter.ToPosix(protection), flags, -1, 0);

        if (result != PosixNativeMethods.MAP_FAILED && result != IntPtr.Zero)
            return result;

        bool wantsWriteAndExecute =
            (protection & MemoryProtection.Write) != 0 &&
            (protection & MemoryProtection.Execute) != 0;

        if (!wantsWriteAndExecute)
            return IntPtr.Zero;

        // W^X policies reject PROT_WRITE|PROT_EXEC outright. Map writable; the caller flips it
        // with FinalizeStub once the bytes are in place.
        result = PosixNativeMethods.Map(address, (UIntPtr)size, MemoryProtectionConverter.ToPosix(MemoryProtection.ReadWrite), flags, -1, 0);
        return result == PosixNativeMethods.MAP_FAILED ? IntPtr.Zero : result;
    }

    private static UInt64 WindowsBoundAlloc(UInt64 low, UInt64 high, UInt64 size, MemoryProtection protection)
    {
        NativeMethods.Win32SystemInfo info = default;
        NativeMethods.GetNativeSystemInfo(&info);
        UInt64 granularity = info.dwAllocationGranularity;

        UInt64 applicationMin = (UInt64)info.lpMinimumApplicationAddress;
        UInt64 applicationMax = (UInt64)info.lpMaximumApplicationAddress;

        low = Math.Max(AlignUp(low, granularity), AlignUp(applicationMin, granularity));
        high = Math.Min(high & ~(granularity - 1), applicationMax & ~(granularity - 1));

        if (low >= high || size > high - low)
            return 0;

        UInt64 viaVirtualAlloc2 = TryVirtualAlloc2(low, high, size, protection);
        if (viaVirtualAlloc2 != 0)
        {
            if (IsWithin(viaVirtualAlloc2, low, high, size))
                return viaVirtualAlloc2;

            // Defensive only: the address-requirements contract should make this impossible.
            NativeMethods.VirtualFree((IntPtr)viaVirtualAlloc2, UIntPtr.Zero, (uint)NativeMethods.Win32AllocationType.MEM_RELEASE);
        }

        // VirtualAlloc2 can fail in a fragmented process even when a suitable hole exists.
        // Keep the explicit region walk from the pre-refactor allocator as the reliable fallback.
        return WindowsBoundAllocFallback(low, high, size, protection, granularity);
    }

    /// <summary>
    /// Walks the current process address space and tries every allocation-granularity-aligned free range inside <c>[low, high)</c>. 
    /// Kept separate so the fallback can be regression-tested independently of VirtualAlloc2.
    /// </summary>
    internal static UInt64 WindowsBoundAllocFallback(
        UInt64 low,
        UInt64 high,
        UInt64 size,
        MemoryProtection protection,
        UInt64 granularity)
    {
        if (granularity == 0 || (granularity & (granularity - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(granularity), "Allocation granularity must be a power of two.");
        if (low >= high || size == 0 || size > high - low)
            return 0;

        UInt64 current = low;
        NativeMethods.Win32MEMORY_BASIC_INFORMATION mbi = default;

        while (current < high && size <= high - current)
        {
            if (NativeMethods.VirtualQueryEx(InstructionCache.CurrentProcessHandle, (IntPtr)current, &mbi, (uint)Marshal.SizeOf<NativeMethods.Win32MEMORY_BASIC_INFORMATION>()) == 0)
            {
                break;
            }

            UInt64 regionBase = (UInt64)mbi.BaseAddress;
            UInt64 regionSize = (UInt64)mbi.RegionSize;
            if (regionSize == 0 || regionBase > UInt64.MaxValue - regionSize)
                break;

            UInt64 regionEnd = regionBase + regionSize;

            if (mbi.State == (uint)NativeMethods.Win32AllocationType.MEM_FREE)
            {
                // VirtualQuery returns the base of the containing region, which may be below the requested lower bound. The refactor accidentally aligned regionBase alone,
                // allowing allocations outside the +/-2 GB window on fragmented processes.
                UInt64 candidate = AlignUp(Math.Max(current, regionBase), granularity);
                UInt64 candidateLimit = Math.Min(regionEnd, high);

                if (candidate >= low && candidate <= candidateLimit && size <= candidateLimit - candidate)
                {
                    IntPtr allocated = NativeMethods.VirtualAlloc((IntPtr)candidate, (UIntPtr)size, NativeMethods.Win32AllocationType.MEM_COMMIT | NativeMethods.Win32AllocationType.MEM_RESERVE, (NativeMethods.Win32MemoryPermissions)MemoryProtectionConverter.ToWin32(protection));
                    if (allocated != IntPtr.Zero && IsWithin((UInt64)allocated, low, high, size))
                        return (UInt64)allocated;

                    // A racing allocator may have taken this exact hole. Move one granularity unit forward and keep looking instead of abandoning the whole range.
                    if (candidate <= UInt64.MaxValue - granularity)
                    {
                        current = candidate + granularity;
                        continue;
                    }
                }
            }

            UInt64 next = AlignUp(regionEnd, granularity);
            if (next <= current)
                break;
            current = next;
        }

        return 0;
    }

    private static UInt64 TryVirtualAlloc2(UInt64 low, UInt64 high, UInt64 size, MemoryProtection protection)
    {
        try
        {
            if (NativeMethods.VirtualAlloc2 == null)
                return 0;

            NativeMethods.Win32MEM_ADDRESS_REQUIREMENTS requirements = new()
            {
                LowestStartingAddress = (IntPtr)low,
                HighestEndingAddress = (IntPtr)(high - 1),
                Alignment = IntPtr.Zero,
            };

            NativeMethods.Win32MEM_EXTENDED_PARAMETER parameter = new()
            {
                Type = (UInt64)NativeMethods.Win32MEM_EXTENDED_PARAMETER_TYPE.MemExtendedParameterAddressRequirements,
            };
            parameter.Value.Pointer = (IntPtr)(&requirements);

            // A null process handle is the documented current-process form and matches the allocator used before the refactor.
            IntPtr result = NativeMethods.VirtualAlloc2(IntPtr.Zero, IntPtr.Zero, (UIntPtr)size, NativeMethods.Win32AllocationType.MEM_COMMIT | NativeMethods.Win32AllocationType.MEM_RESERVE, (NativeMethods.Win32MemoryPermissions)MemoryProtectionConverter.ToWin32(protection), &parameter, 1);
            return (UInt64)result;
        }
        catch
        {
            // VirtualAlloc2 is Windows 10+; older hosts fall through to the VirtualQuery walk.
            return 0;
        }
    }

    private static UInt64 AlignUp(UInt64 value, UInt64 alignment)
    {
        UInt64 mask = alignment - 1;
        if (value > UInt64.MaxValue - mask)
            return UInt64.MaxValue & ~mask;

        return (value + mask) & ~mask;
    }
}
