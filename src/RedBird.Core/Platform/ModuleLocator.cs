using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RedBird.Core.Platform;

/// <summary>
/// Answers one question: is this address inside a loaded module, or is it anonymous memory?
/// </summary>
/// <remarks>
/// <para>
/// That distinction is what separates a thunk from an existing hook. A compiler-emitted thunk an import stub, an incremental-link stub, a hot-patch stub jumps somewhere inside a mapped image. 
/// A detour's jump goes to a trampoline, a code cave slot, or a JIT-compiled marshalling stub, none of which belong to a module. 
/// Following the first is helpful.
/// Following the second lands you inside somebody else's callback.
/// </para>
/// </remarks>
public static unsafe class ModuleLocator
{
    private const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x00000004;
    private const uint GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x00000002;

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetModuleHandleExW(uint dwFlags, IntPtr lpModuleName, out IntPtr phModule);

    /// <summary><c>Dl_info</c> as declared in <c>dlfcn.h</c>: four pointers.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DlInfo
    {
        public IntPtr FileName;
        public IntPtr FileBase;
        public IntPtr SymbolName;
        public IntPtr SymbolAddress;
    }

    private static readonly delegate* unmanaged[Cdecl]<IntPtr, DlInfo*, int> _dladdr = ResolveDladdr();

    private static delegate* unmanaged[Cdecl]<IntPtr, DlInfo*, int> ResolveDladdr()
    {
        if (PlatformInfo.IsWindows)
            return null;

#if NET8_0_OR_GREATER
        try
        {
            IntPtr handle = NativeLibrary.GetMainProgramHandle();

            if (handle != IntPtr.Zero && NativeLibrary.TryGetExport(handle, "dladdr", out IntPtr address))
                return (delegate* unmanaged[Cdecl]<IntPtr, DlInfo*, int>)address;
        }
        catch
        {
            // Fall through to the /proc/self/maps path.
        }
#endif

        return null;
    }

    /// <summary>
    /// Finds the module containing <paramref name="address"/>.
    /// </summary>
    /// <param name="address">Address to locate.</param>
    /// <param name="moduleBase">Load address of the containing module, when there is one.</param>
    /// <returns>False when the address is in anonymous memory, a trampoline, a JIT stub, a heap allocation.</returns>
    public static bool TryGetContainingModule(UInt64 address, out IntPtr moduleBase)
    {
        moduleBase = IntPtr.Zero;

        if (address == 0)
            return false;

        if (PlatformInfo.IsWindows)
        {
            // UNCHANGED_REFCOUNT: this is a query, not an acquisition. Without it every call leaks a reference on the module.
            return GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (IntPtr)address, out moduleBase) && moduleBase != IntPtr.Zero;
        }

        if (_dladdr is not null)
        {
            DlInfo info = default;

            if (_dladdr((IntPtr)address, &info) != 0 && info.FileBase != IntPtr.Zero)
            {
                moduleBase = info.FileBase;
                return true;
            }

            return false;
        }

        // No dladdr: a file-backed mapping is a module, an anonymous one is not.
        if (ProcMaps.TryFindRegion(address, out MappedRegion region) && !string.IsNullOrEmpty(region.Path) && region.Path![0] != '[')
        {
            moduleBase = (IntPtr)region.Start;
            return true;
        }

        return false;
    }

    /// <summary>True when both addresses live in the same loaded module.</summary>
    public static bool AreInSameModule(UInt64 first, UInt64 second)
        => TryGetContainingModule(first, out IntPtr a)
        && TryGetContainingModule(second, out IntPtr b)
        && a == b;
}
