using System;
using System.Runtime.InteropServices;

namespace RedBird.Core.PInvoke;

#pragma warning disable SYSLIB1054

/// <summary>
/// Direct Win32 imports used by ordinary managed allocation and export-resolution code.
/// </summary>
internal static unsafe class WindowsMemoryMethods
{
    [DllImport("kernel32.dll")]
    internal static extern void GetSystemInfo(out NativeMethods.Win32SystemInfo systemInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr VirtualAlloc(
        IntPtr address,
        UIntPtr size,
        NativeMethods.Win32AllocationType allocationType,
        NativeMethods.Win32MemoryPermissions protection);

    [DllImport("kernel32.dll", EntryPoint = "VirtualAlloc2", SetLastError = true)]
    internal static extern IntPtr VirtualAlloc2(
        IntPtr process,
        IntPtr baseAddress,
        UIntPtr size,
        NativeMethods.Win32AllocationType allocationType,
        NativeMethods.Win32MemoryPermissions protection,
        NativeMethods.Win32MEM_EXTENDED_PARAMETER* extendedParameters,
        uint parameterCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualProtect(
        IntPtr address,
        UIntPtr size,
        NativeMethods.Win32MemoryPermissions newProtection,
        out NativeMethods.Win32MemoryPermissions oldProtection);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern UIntPtr VirtualQuery(
        IntPtr address,
        out NativeMethods.Win32MEMORY_BASIC_INFORMATION information,
        UIntPtr informationLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr GetProcAddress(IntPtr module, string exportName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, EntryPoint = "GetModuleHandleA", SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
}

#pragma warning restore SYSLIB1054
