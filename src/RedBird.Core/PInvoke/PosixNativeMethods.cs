using System;
using System.Runtime.InteropServices;
using RedBird.Core.Platform;

namespace RedBird.Core.PInvoke;

#pragma warning disable CA2101, SYSLIB1054

/// <summary>
/// POSIX memory syscalls, resolved at first use.
/// </summary>
public static unsafe class PosixNativeMethods
{
    // --- mmap protection bits (identical across Linux, macOS and the BSDs) ---

    /// <summary>PROT_NONE.</summary>
    public const int PROT_NONE = 0x0;

    /// <summary>PROT_READ.</summary>
    public const int PROT_READ = 0x1;

    /// <summary>PROT_WRITE.</summary>
    public const int PROT_WRITE = 0x2;

    /// <summary>PROT_EXEC.</summary>
    public const int PROT_EXEC = 0x4;

    // --- mmap flags ---

    /// <summary>MAP_PRIVATE.</summary>
    public const int MAP_PRIVATE = 0x02;

    /// <summary>MAP_FIXED. Replaces any existing mapping: dangerous unless the range is known free.</summary>
    public const int MAP_FIXED = 0x10;

    /// <summary>
    /// MAP_ANONYMOUS. <b>0x20 on Linux, 0x1000 on macOS and the BSDs</b>
    /// </summary>
    public static int MAP_ANONYMOUS { get; } = PlatformInfo.IsLinux ? 0x20 : 0x1000;

    /// <summary>
    /// MAP_FIXED_NOREPLACE (Linux 4.17+). 
    /// Places the mapping at the requested address or fails. Returns EEXIST when taken.
    /// Older kernels ignore the flag and fall back to hint behaviour, which is why the caller still has to verify the returned address.
    /// </summary>
    public const int MAP_FIXED_NOREPLACE = 0x100000;

    /// <summary>The value <c>mmap</c> returns on failure. <b>Not</b> null.</summary>
    public static IntPtr MAP_FAILED => new(-1);

    /// <summary>True when the syscall bindings resolved successfully.</summary>
    public static bool IsAvailable { get; }

#if NET8_0_OR_GREATER
    private static readonly delegate* unmanaged[Cdecl]<IntPtr, UIntPtr, int, int, int, long, IntPtr> _mmap;
    private static readonly delegate* unmanaged[Cdecl]<IntPtr, UIntPtr, int> _munmap;
    private static readonly delegate* unmanaged[Cdecl]<IntPtr, UIntPtr, int, int> _mprotect;
    private static readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> _clearCache;
#else
    // Mono resolves the conventional "libc" name to the host C library on Linux, macOS and the BSDs.
    // Keeping this fallback here makes the net48 target usable on 32-bit POSIX, where modern .NET runtimes are generally unavailable.
    private static class Legacy
    {
        [DllImport("libc", EntryPoint = "mmap", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        internal static extern IntPtr Map(IntPtr address, UIntPtr length, int protection, int flags, int fd, long offset);

        [DllImport("libc", EntryPoint = "munmap", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        internal static extern int Unmap(IntPtr address, UIntPtr length);

        [DllImport("libc", EntryPoint = "mprotect", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        internal static extern int Protect(IntPtr address, UIntPtr length, int protection);

        [DllImport("libc", EntryPoint = "__clear_cache", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ClearCache(IntPtr start, IntPtr end);
    }
#endif

    static PosixNativeMethods()
    {
        if (!PlatformInfo.IsPosix)
        {
            IsAvailable = false;
            return;
        }

#if NET8_0_OR_GREATER
        if (!TryResolveLibc(out IntPtr libc))
        {
            IsAvailable = false;
            return;
        }

        if (!NativeLibrary.TryGetExport(libc, "mmap", out IntPtr mmapPtr) ||
            !NativeLibrary.TryGetExport(libc, "munmap", out IntPtr munmapPtr) ||
            !NativeLibrary.TryGetExport(libc, "mprotect", out IntPtr mprotectPtr))
        {
            IsAvailable = false;
            return;
        }

        _mmap = (delegate* unmanaged[Cdecl]<IntPtr, UIntPtr, int, int, int, long, IntPtr>)mmapPtr;
        _munmap = (delegate* unmanaged[Cdecl]<IntPtr, UIntPtr, int>)munmapPtr;
        _mprotect = (delegate* unmanaged[Cdecl]<IntPtr, UIntPtr, int, int>)mprotectPtr;

        // __clear_cache lives in libgcc/compiler-rt, optional.
        _clearCache = NativeLibrary.TryGetExport(libc, "__clear_cache", out IntPtr clearPtr)
            ? (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)clearPtr
            : null;

        IsAvailable = true;
#else
        // The declarations are resolved lazily by Mono on first invocation.
        // Mark the surface as available now; a missing libc is reported by the invoking method.
        IsAvailable = true;
#endif
    }

#if NET8_0_OR_GREATER
    /// <summary>
    /// Resolves libc.
    /// </summary>
    private static bool TryResolveLibc(out IntPtr handle)
    {
        try
        {
            handle = NativeLibrary.GetMainProgramHandle();
            if (handle != IntPtr.Zero && NativeLibrary.TryGetExport(handle, "mprotect", out _))
                return true;
        }
        catch
        {
            // GetMainProgramHandle is unsupported on some runtimes; fall through to the tests.
        }

        string[] candidates = PlatformInfo.IsMacOS
            ? ["libSystem.dylib", "libc.dylib"]
            : ["libc.so.6", "libc.so.7", "libc"];

        foreach (string candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out handle) && handle != IntPtr.Zero)
                return true;
        }

        handle = IntPtr.Zero;
        return false;
    }
#endif

    /// <summary>mmap(2).</summary>
    public static IntPtr Map(IntPtr address, UIntPtr length, int protection, int flags, int fd, long offset)
    {
#if NET8_0_OR_GREATER
        EnsureAvailable();
        return _mmap(address, length, protection, flags, fd, offset);
#else
        EnsureLegacyAvailable();
        return Legacy.Map(address, length, protection, flags, fd, offset);
#endif
    }

    /// <summary>munmap(2).</summary>
    public static bool Unmap(IntPtr address, UIntPtr length)
    {
#if NET8_0_OR_GREATER
        EnsureAvailable();
        return _munmap(address, length) == 0;
#else
        EnsureLegacyAvailable();
        return Legacy.Unmap(address, length) == 0;
#endif
    }

    /// <summary>
    /// mprotect(2). 
    /// <paramref name="address"/> must be page-aligned; the caller is responsible for that, since mprotect fails with EINVAL otherwise rather than rounding.
    /// </summary>
    public static bool Protect(IntPtr address, UIntPtr length, int protection)
    {
#if NET8_0_OR_GREATER
        EnsureAvailable();
        return _mprotect(address, length, protection) == 0;
#else
        EnsureLegacyAvailable();
        return Legacy.Protect(address, length, protection) == 0;
#endif
    }

    /// <summary>
    /// Invokes <c>__clear_cache</c> when the toolchain provides it.
    /// </summary>
    /// <returns>False when the symbol is unavailable, which is normal and harmless on x86.</returns>
    public static bool TryClearCache(IntPtr start, IntPtr end)
    {
#if NET8_0_OR_GREATER
        if (!IsAvailable || _clearCache is null)
            return false;

        _clearCache(start, end);
        return true;
#else
        if (!PlatformInfo.IsPosix)
            return false;

        try
        {
            Legacy.ClearCache(start, end);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
#endif
    }

#if NET8_0_OR_GREATER
    private static void EnsureAvailable()
    {
        if (!IsAvailable)
            throw new PlatformNotSupportedException("Could not resolve mmap/mprotect from libc on this platform.");
    }
#endif

#if !NET8_0_OR_GREATER
    private static void EnsureLegacyAvailable()
    {
        if (!PlatformInfo.IsPosix)
            throw new PlatformNotSupportedException("POSIX memory syscalls are unavailable on this host.");
    }
#endif
}

#pragma warning restore CA2101, SYSLIB1054
