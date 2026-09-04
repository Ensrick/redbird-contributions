using System;
using System.Runtime.InteropServices;
using RedBird.Core.PInvoke;
using RedBird.Core.Platform;

namespace RedBird.Core.Utilities;

/// <summary>
/// Resolves exported symbols by name, so a hook can be registered as <c>HookTarget.FromExport("DLL_ImportAIV")</c> instead of the caller resolving an address
/// itself, and so a missing export is reported as a normal transaction failure.
/// </summary>
public static class NativeExports
{
    /// <summary>Resolves <paramref name="exportName"/> in the module loaded at <paramref name="module"/>.</summary>
    /// <returns>False when the module handle is null or the export does not exist.</returns>
    public static bool TryResolve(IntPtr module, string exportName, out UInt64 address)
    {
        address = 0;

        if (module == IntPtr.Zero || string.IsNullOrEmpty(exportName))
            return false;

        if (PlatformInfo.IsWindows)
        {
            IntPtr resolved = WindowsMemoryMethods.GetProcAddress(module, exportName);
            if (resolved == IntPtr.Zero)
                return false;

            address = (UInt64)resolved.ToInt64();
            return true;
        }

        if (ProcMaps.TryGetModulePath((UInt64)module.ToInt64(), out string path))
            return TryResolveByModuleName(path, exportName, out address);

        // No map to consult (macOS, or an anonymous mapping).
        // The process-global symbol scope is the best remaining option: it finds exports of anything already loaded, though it cannot distinguish between two modules exporting the same name.
        return TryResolveInProcess(exportName, out address);
    }

    /// <summary>
    /// Resolves an export from a module identified by name or path. Portable, and the preferred
    /// overload.
    /// </summary>
    public static bool TryResolveByModuleName(string moduleName, string exportName, out UInt64 address)
    {
        address = 0;

        if (string.IsNullOrEmpty(moduleName) || string.IsNullOrEmpty(exportName))
            return false;

#if NET8_0_OR_GREATER
        if (!NativeLibrary.TryLoad(moduleName, out IntPtr handle) || handle == IntPtr.Zero)
            return false;

        if (!NativeLibrary.TryGetExport(handle, exportName, out IntPtr resolved) || resolved == IntPtr.Zero)
            return false;

        address = (UInt64)resolved.ToInt64();
        return true;
#else
        return false;
#endif
    }

    /// <summary>Resolves an export from anything already present in the process's global symbol scope.</summary>
    public static bool TryResolveInProcess(string exportName, out UInt64 address)
    {
        address = 0;

        if (string.IsNullOrEmpty(exportName))
            return false;

#if NET8_0_OR_GREATER
        try
        {
            IntPtr main = NativeLibrary.GetMainProgramHandle();
            if (main != IntPtr.Zero && NativeLibrary.TryGetExport(main, exportName, out IntPtr resolved) && resolved != IntPtr.Zero)
            {
                address = (UInt64)resolved.ToInt64();
                return true;
            }
        }
        catch
        {
            // GetMainProgramHandle is not available on every runtime.
        }
#endif

        return false;
    }
}
