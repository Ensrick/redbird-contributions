using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using RedBird.Core.PInvoke;
using RedBird.Core.Platform;

namespace RedBird.Core.Utilities;

/// <summary>
/// Finds loaded modules by name.
/// </summary>
public static class ModuleHelper
{

    /// <summary>
    /// Finds the load address of <paramref name="moduleName"/>. 
    /// Matching is on the file name and is case-insensitive on Windows, case-sensitive on POSIX, as each platform's loader is.
    /// </summary>
    public static bool TryGetModuleBaseAddress(string moduleName, out IntPtr baseAddress)
    {
        baseAddress = IntPtr.Zero;

        if (string.IsNullOrEmpty(moduleName))
            return false;

        return PlatformInfo.IsWindows
            ? TryGetWindowsModule(moduleName, out baseAddress)
            : TryGetPosixModule(moduleName, out baseAddress);
    }

    private static bool TryGetWindowsModule(string moduleName, out IntPtr baseAddress)
    {
        baseAddress = IntPtr.Zero;

#if NET8_0_OR_GREATER
        if (NativeLibrary.TryLoad(moduleName, out baseAddress))
            return true;
#endif
        unsafe
        {
            baseAddress = WindowsMethods.GetModuleHandleA(moduleName);
            if (baseAddress != IntPtr.Zero)
                return true;
        }

        // If above fails for some reason, try asking the DotNet runtime.
        using Process current = Process.GetCurrentProcess();
        foreach (ProcessModule module in current.Modules)
        {
            if (module.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
            {
                baseAddress = module.BaseAddress;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetPosixModule(string moduleName, out IntPtr baseAddress)
    {
        baseAddress = IntPtr.Zero;

        if (!ProcMaps.IsAvailable)
            return false;

        // The first mapping of a shared object is its load bias, which is what callers mean by "base address". Later mappings of the same file are the other PT_LOAD segments, so only the lowest one counts.
        UInt64 lowest = UInt64.MaxValue;

        foreach (MappedRegion region in ProcMaps.Read())
        {
            if (string.IsNullOrEmpty(region.Path))
                continue;

            string fileName = Path.GetFileName(region.Path!);
            bool matches =
                string.Equals(fileName, moduleName, StringComparison.Ordinal) ||
                string.Equals(region.Path, moduleName, StringComparison.Ordinal) ||
                fileName.StartsWith(moduleName + ".so", StringComparison.Ordinal);

            if (matches && region.Start < lowest)
                lowest = region.Start;
        }

        if (lowest == UInt64.MaxValue)
            return false;

        baseAddress = (IntPtr)lowest;
        return true;
    }

    /// <summary>Enumerates the distinct modules mapped into the process, with their load addresses.</summary>
    public static IReadOnlyList<(string Path, IntPtr BaseAddress)> GetLoadedModules()
    {
        List<(string, IntPtr)> modules = [];

        if (PlatformInfo.IsWindows)
        {
            using Process current = Process.GetCurrentProcess();
            foreach (ProcessModule module in current.Modules)
                modules.Add((module.FileName, module.BaseAddress));

            return modules;
        }

        HashSet<string> seen = [];
        foreach (MappedRegion region in ProcMaps.Read())
        {
            if (string.IsNullOrEmpty(region.Path) || region.Path![0] == '[')
                continue;

            if (seen.Add(region.Path!))
                modules.Add((region.Path!, (IntPtr)region.Start));
        }

        return modules;
    }
}
