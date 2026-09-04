using System;
using System.Runtime.InteropServices;
using RedBird.Core.Utilities;

namespace RedBird.Core.PInvoke;

#pragma warning disable CA2101, SYSLIB1054

/// <summary>
/// Contains platform-specific P/Invoke declarations and constants for memory management and system information.
/// </summary>
public unsafe static class NativeMethods
{
    // --- Platform Agnostic Constants and Helpers ---
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static readonly bool IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    internal const string KERNELDLL_NAME = "kernel32.dll";
    internal const string KERNELBASE_NAME = "kernelbase.dll";

    public enum Win32MemoryPermissions : UInt32
    {
        PAGE_EXECUTE = 0x10,
        PAGE_EXECUTE_READ = 0x20,
        PAGE_EXECUTE_READWRITE = 0x40,
        PAGE_EXECUTE_WRITECOPY = 0x80,
        PAGE_NOACCESS = 0x01,
        PAGE_READONLY = 0x02,
        PAGE_READWRITE = 0x04,
        PAGE_WRITECOPY = 0x08,
    }

    public enum Win32AllocationType : UInt32
    {
        MEM_COMMIT = 0x1000,
        MEM_RESERVE = 0x2000,
        MEM_RELEASE = 0x8000,
        MEM_DECOMMIT = 0x4000,
        MEM_FREE = 0x10000,
        MEM_RESET = 0x80000,
        MEM_LARGE_PAGES = 0x20000000,
        MEM_PHYSICAL = 0x400000,
        MEM_TOP_DOWN = 0x100000,
        MEM_WRITE_WATCH = 0x200000
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Win32SystemInfo
    {
        public Win32PROCESSOR_ARCHITECTRURE wProcessorArchitecture;
        public ushort wReserved;
        public uint dwPageSize;
        public IntPtr lpMinimumApplicationAddress;
        public IntPtr lpMaximumApplicationAddress;
        public IntPtr dwActiveProcessorMask;
        public uint dwNumberOfProcessors;
        public uint dwProcessorType;
        public uint dwAllocationGranularity;
        public ushort wProcessorLevel;
        public ushort wProcessorRevision;
    }


    public enum Win32PROCESSOR_ARCHITECTRURE : ushort
    {
        /// <summary>
        /// x64 (AMD or Intel)
        /// </summary>
        AMD64 = 9,

        /// <summary>
        /// ARM
        /// </summary>
        ARM = 5,

        /// <summary>
        /// ARM64
        /// </summary>
        ARM64 = 12,

        /// <summary>
        /// Intel Itanium-based
        /// </summary>
        IA64 = 6,

        /// <summary>
        /// x86
        /// </summary>
        INTEL = 0,

        /// <summary>
        /// Unknown architecture
        /// </summary>
        UNKNOWN = 0xFFFF
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Win32MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Win32MEM_ADDRESS_REQUIREMENTS
    {
        public IntPtr LowestStartingAddress;
        public IntPtr HighestEndingAddress;
        public IntPtr Alignment;
    }

    public enum Win32MEM_EXTENDED_PARAMETER_TYPE
    {
        MemExtendedParameterInvalidType = 0,
        MemExtendedParameterAddressRequirements,
        MemExtendedParameterNumaNode,
        MemExtendedParameterPartitionHandle,
        MemExtendedParameterUserPhysicalHandle,
        MemExtendedParameterAttributeFlags,
        MemExtendedParameterMax
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct Win32MEM_EXTENDED_PARAMETER
    {
        public ulong Type;   // low 8 bits = MEM_EXTENDED_PARAMETER_TYPE, high bits = Reserved

        public Win32MEM_EXTENDED_PARAMETER_VALUE Value;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct Win32MEM_EXTENDED_PARAMETER_VALUE
    {
        [FieldOffset(0)]
        public ulong ULong64;

        [FieldOffset(0)]
        public IntPtr Pointer;

        [FieldOffset(0)]
        public UIntPtr Size;

        [FieldOffset(0)]
        public IntPtr Handle;
    }

    // --- Linux Specific Constants ---
    internal const string LIBC_NAME = "c";

    // Protections for mmap
    internal const int PROT_NONE = 0x0;
    internal const int PROT_READ = 0x1;
    internal const int PROT_WRITE = 0x2;
    internal const int PROT_EXEC = 0x4;

    // Flags for mmap
    internal const int MAP_PRIVATE = 0x02;
    internal const int MAP_ANONYMOUS = 0x20;

#if NET8_0_OR_GREATER

    internal static delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, UIntPtr, Win32AllocationType, Win32MemoryPermissions, IntPtr> VirtualAlloc;
    internal static delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, IntPtr, UIntPtr, Win32AllocationType, Win32MemoryPermissions, Win32MEM_EXTENDED_PARAMETER*, uint, IntPtr> VirtualAlloc2;
    internal static delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, IntPtr, UIntPtr, Win32AllocationType, Win32MemoryPermissions, IntPtr> VirtualAllocEx;
    internal static delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, UIntPtr, UInt32, bool> VirtualFree;
    internal static delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, IntPtr, UInt32, Win32MemoryPermissions, Win32MemoryPermissions*, bool> VirtualProtectEx;
    internal static delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, UInt32, Win32MemoryPermissions, Win32MemoryPermissions*, bool> VirtualProtect;
    internal static delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, IntPtr, byte*, UInt32, UInt32*, bool> ReadProcessMemory;
    internal static delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, IntPtr, byte*, UInt32, UInt32*, bool> WriteProcessMemory;
    internal static delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, IntPtr, IntPtr> GetProcAddress;
    internal static delegate* unmanaged[Stdcall, SuppressGCTransition]<Win32SystemInfo*, void> GetNativeSystemInfo;
    internal static delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, IntPtr, Win32MEMORY_BASIC_INFORMATION*, uint, int> VirtualQueryEx;
    internal static delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, IntPtr, UIntPtr, byte> FlushInstructionCache;
    internal static delegate* unmanaged[Stdcall, SuppressGCTransition]<string, IntPtr> GetModuleHandleA;

    // --- Linux Function Pointers ---
    // void *mmap(void *addr, size_t length, int prot, int flags, int fd, off_t offset);
    internal static delegate* unmanaged[Cdecl, SuppressGCTransition]<IntPtr, UIntPtr, int, int, int, long, IntPtr> mmap;
    // int munmap(void *addr, size_t length);
    internal static delegate* unmanaged[Cdecl, SuppressGCTransition]<IntPtr, UIntPtr, int> munmap;
    // int mprotect(void *addr, size_t len, int prot);
    internal static delegate* unmanaged[Cdecl, SuppressGCTransition]<IntPtr, UIntPtr, int, int> mprotect;
#else

    internal static delegate* unmanaged[Stdcall]<IntPtr, UIntPtr , Win32AllocationType, Win32MemoryPermissions, IntPtr> VirtualAlloc;
    internal static delegate* unmanaged[Stdcall]<IntPtr, IntPtr, UIntPtr, Win32AllocationType, Win32MemoryPermissions, Win32MEM_EXTENDED_PARAMETER*, uint, IntPtr> VirtualAlloc2;
    internal static delegate* unmanaged[Stdcall]<IntPtr, IntPtr, UIntPtr , Win32AllocationType, Win32MemoryPermissions, IntPtr> VirtualAllocEx;
    internal static delegate* unmanaged[Stdcall]<IntPtr, UIntPtr , UInt32, bool> VirtualFree;
    internal static delegate* unmanaged[Stdcall]<IntPtr, IntPtr, UInt32, Win32MemoryPermissions, Win32MemoryPermissions*, bool> VirtualProtectEx;
    internal static delegate* unmanaged[Stdcall]<IntPtr, UInt32, Win32MemoryPermissions, Win32MemoryPermissions*, bool> VirtualProtect;
    internal static delegate* unmanaged[Stdcall]<IntPtr, IntPtr, byte*, UInt32, UInt32*, bool> ReadProcessMemory;
    internal static delegate* unmanaged[Stdcall]<IntPtr, IntPtr, byte*, UInt32, UInt32*, bool> WriteProcessMemory;
    internal static delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr> GetProcAddress;
    internal static delegate* unmanaged[Stdcall]<Win32SystemInfo*, void> GetNativeSystemInfo;
    internal static delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Win32MEMORY_BASIC_INFORMATION*, uint, int> VirtualQueryEx;
    internal static delegate* unmanaged[Stdcall]<IntPtr, IntPtr, UIntPtr, byte> FlushInstructionCache;
    internal static delegate* unmanaged[Stdcall]<string, IntPtr> GetModuleHandleA;

    // --- Linux Function Pointers ---
    internal static delegate* unmanaged[Cdecl]<IntPtr, UIntPtr, int, int, int, long, IntPtr> mmap;
    internal static delegate* unmanaged[Cdecl]<IntPtr, UIntPtr, int> munmap;
    internal static delegate* unmanaged[Cdecl]<IntPtr, UIntPtr, int, int> mprotect;
#endif
    static NativeMethods()
    {
        if (IsWindows)
        {
            if (!ModuleHelper.TryGetModuleBaseAddress(KERNELDLL_NAME, out IntPtr kernel32))
                throw new Exception("could not find kernel32 image base");

            if (!ModuleHelper.TryGetModuleBaseAddress(KERNELBASE_NAME, out IntPtr kernelbase))
                throw new Exception("could not find kernelbase image base");

#if NET8_0_OR_GREATER
            WriteProcessMemory = (delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, IntPtr, byte*, UInt32, UInt32*, bool>)NativeLibrary.GetExport(kernel32, "WriteProcessMemory");
            ReadProcessMemory = (delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, IntPtr, byte*, UInt32, UInt32*, bool>)NativeLibrary.GetExport(kernel32, "ReadProcessMemory");
            VirtualAlloc = (delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, UIntPtr, Win32AllocationType, Win32MemoryPermissions, IntPtr>)NativeLibrary.GetExport(kernel32, "VirtualAlloc");
            // VirtualAlloc2 is optional. Hosts that do not export it use the explicit VirtualQuery/VirtualAlloc walk in NativeMemoryManager instead.
            if (NativeLibrary.TryGetExport(kernelbase, "VirtualAlloc2", out IntPtr virtualAlloc2))
            {
                VirtualAlloc2 = (delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, IntPtr, UIntPtr, Win32AllocationType, Win32MemoryPermissions, Win32MEM_EXTENDED_PARAMETER*, uint, IntPtr>)virtualAlloc2;
            }
            VirtualAllocEx = (delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, IntPtr, UIntPtr, Win32AllocationType, Win32MemoryPermissions, IntPtr>)NativeLibrary.GetExport(kernel32, "VirtualAllocEx");
            VirtualFree = (delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, UIntPtr, UInt32, bool>)NativeLibrary.GetExport(kernel32, "VirtualFree");
            VirtualProtect = (delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, UInt32, Win32MemoryPermissions, Win32MemoryPermissions*, bool>)NativeLibrary.GetExport(kernel32, "VirtualProtect");
            VirtualProtectEx = (delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, IntPtr, UInt32, Win32MemoryPermissions, Win32MemoryPermissions*, bool>)NativeLibrary.GetExport(kernel32, "VirtualProtectEx");
            GetProcAddress = (delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, IntPtr, IntPtr>)NativeLibrary.GetExport(kernel32, "GetProcAddress");
            GetNativeSystemInfo = (delegate* unmanaged[Stdcall, SuppressGCTransition]<Win32SystemInfo*, void>)NativeLibrary.GetExport(kernel32, "GetNativeSystemInfo");
            VirtualQueryEx = (delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, IntPtr, Win32MEMORY_BASIC_INFORMATION*, uint, int>)NativeLibrary.GetExport(kernel32, "VirtualQueryEx");
            FlushInstructionCache = (delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, IntPtr, UIntPtr, byte>)NativeLibrary.GetExport(kernel32, "FlushInstructionCache");
            GetModuleHandleA = (delegate* unmanaged[Stdcall, SuppressGCTransition]<string, IntPtr>)NativeLibrary.GetExport(kernel32, "GetModuleHandleA");

#else
            WriteProcessMemory = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, byte*, UInt32, UInt32*, bool>)FrameworkLoader.GetProcAddress(kernel32, "WriteProcessMemory");
            ReadProcessMemory = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, byte*, UInt32, UInt32*, bool>)FrameworkLoader.GetProcAddress(kernel32, "ReadProcessMemory");
            VirtualAlloc = (delegate* unmanaged[Stdcall]<IntPtr, UIntPtr , Win32AllocationType, Win32MemoryPermissions, IntPtr>)FrameworkLoader.GetProcAddress(kernel32, "VirtualAlloc");
            VirtualAlloc2 = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, UIntPtr, Win32AllocationType, Win32MemoryPermissions, Win32MEM_EXTENDED_PARAMETER*, uint, IntPtr>)FrameworkLoader.GetProcAddress(kernelbase, "VirtualAlloc2");
            VirtualAllocEx = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, UIntPtr , Win32AllocationType, Win32MemoryPermissions, IntPtr>)FrameworkLoader.GetProcAddress(kernel32, "VirtualAllocEx");
            VirtualFree = (delegate* unmanaged[Stdcall]<IntPtr, UIntPtr , UInt32, bool>)FrameworkLoader.GetProcAddress(kernel32, "VirtualFree");
            VirtualProtect = (delegate* unmanaged[Stdcall]<IntPtr, UInt32, Win32MemoryPermissions, Win32MemoryPermissions*, bool>)FrameworkLoader.GetProcAddress(kernel32, "VirtualProtect");
            VirtualProtectEx = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, UInt32, Win32MemoryPermissions, Win32MemoryPermissions*, bool>)FrameworkLoader.GetProcAddress(kernel32, "VirtualProtectEx");
            GetProcAddress = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr>)FrameworkLoader.GetProcAddress(kernel32, "GetProcAddress");
            GetNativeSystemInfo = (delegate* unmanaged[Stdcall]<Win32SystemInfo*, void>)FrameworkLoader.GetProcAddress(kernel32, "GetNativeSystemInfo");
            VirtualQueryEx = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Win32MEMORY_BASIC_INFORMATION*, uint, int>)FrameworkLoader.GetProcAddress(kernel32, "VirtualQueryEx");
            FlushInstructionCache = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, UIntPtr, byte>)FrameworkLoader.GetProcAddress(kernel32, "FlushInstructionCache");
            GetModuleHandleA = (delegate* unmanaged[Stdcall]<string, IntPtr>)FrameworkLoader.GetProcAddress(kernel32, "GetModuleHandleA");
#endif
        }
        else if (IsLinux)
        {
#if NET8_0_OR_GREATER
            IntPtr libcHandle = NativeLibrary.Load(LIBC_NAME);
            mmap = (delegate* unmanaged[Cdecl, SuppressGCTransition]<IntPtr, UIntPtr, int, int, int, long, IntPtr>)NativeLibrary.GetExport(libcHandle, "mmap");
            munmap = (delegate* unmanaged[Cdecl, SuppressGCTransition]<IntPtr, UIntPtr, int>)NativeLibrary.GetExport(libcHandle, "munmap");
            mprotect = (delegate* unmanaged[Cdecl, SuppressGCTransition]<IntPtr, UIntPtr, int, int>)NativeLibrary.GetExport(libcHandle, "mprotect");
#else
            IntPtr libcHandle = FrameworkLoader.LoadLibrary(string.Empty); // Load current process symbols
            if (libcHandle == IntPtr.Zero) 
                libcHandle = FrameworkLoader.LoadLibrary("libc.so.6"); // Fallback for some systems

            if (libcHandle == IntPtr.Zero) 
                throw new DllNotFoundException("libc");

            mmap = (delegate* unmanaged[Cdecl]<IntPtr, UIntPtr, int, int, int, long, IntPtr>)FrameworkLoader.GetProcAddress(libcHandle, "mmap");
            munmap = (delegate* unmanaged[Cdecl]<IntPtr, UIntPtr, int>)FrameworkLoader.GetProcAddress(libcHandle, "munmap");
            mprotect = (delegate* unmanaged[Cdecl]<IntPtr, UIntPtr, int, int>)FrameworkLoader.GetProcAddress(libcHandle, "mprotect");
#endif
        }
    }

    /// <summary>
    /// A private helper class to provide library loading for .NET Framework.
    /// This code path is not used when compiling for .NET Core 3.1 or newer.
    /// </summary>
    internal static class FrameworkLoader
    {
        internal static IntPtr LoadLibrary(string fileName)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Windows.LoadLibrary(fileName);
            }
            return Unix.dlopen(fileName, Unix.RTLD_NOW);
        }

        internal static IntPtr GetProcAddress(IntPtr handle, string procName)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Windows.GetProcAddress(handle, procName);
            }
            return Unix.dlsym(handle, procName);
        }

        private static class Windows
        {
            public const string KERNELDLL_NAME = "kernel32.dll";

            [DllImport(KERNELDLL_NAME, SetLastError = true, CharSet = CharSet.Ansi)]
            internal static extern IntPtr LoadLibrary(string lpFileName);

            [DllImport(KERNELDLL_NAME, SetLastError = true, CharSet = CharSet.Ansi)]
            internal static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
        }

        private static class Unix
        {
            public const string LIBDL_NAME = "libdl";
            internal const int RTLD_NOW = 2;

            [DllImport(LIBDL_NAME, SetLastError = true)]
            internal static extern IntPtr dlopen(string fileName, int flags);

            [DllImport(LIBDL_NAME, SetLastError = true)]
            internal static extern IntPtr dlsym(IntPtr handle, string symbol);
        }
    }
}

#pragma warning restore CA2101, SYSLIB1054
