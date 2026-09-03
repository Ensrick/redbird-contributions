using System;
using System.Runtime.InteropServices;

namespace RedBird.Core.Platform;

/// <summary>
/// Which host we are running on, resolved once.
/// </summary>
public static class PlatformInfo
{
    /// <summary>Running on Windows.</summary>
    public static bool IsWindows { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>Running on Linux specifically (implies <see cref="IsPosix"/>).</summary>
    public static bool IsLinux { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>Running on macOS specifically (implies <see cref="IsPosix"/>).</summary>
    public static bool IsMacOS { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <summary>Anything that is not Windows: Linux, macOS, FreeBSD, and so on.</summary>
    public static bool IsPosix { get; } = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary><c>/proc/self/maps</c> is readable, i.e. we can inspect our own address space cheaply.</summary>
    public static bool HasProcMaps { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>Page size in bytes.</summary>
    public static int PageSize { get; } = Environment.SystemPageSize;

    /// <summary>
    /// True on x86 and x86-64, where the instruction and data caches are architecturally coherent and self-modifying code needs no explicit cache maintenance beyond a serialising branch.
    /// </summary>
    public static bool HasCoherentInstructionCache { get; } =
        RuntimeInformation.ProcessArchitecture is Architecture.X86 or Architecture.X64;

    /// <summary>
    /// True where the ABI reserves a red zone below <c>rsp</c> that leaf functions may use without  adjusting the stack pointer.
    /// <para>
    /// System V AMD64 (Linux, macOS, the BSDs) reserves 128 bytes; the Microsoft x64 ABI reserves none. 
    /// This is important for every stub that pushes anything: a <c>call</c> or <c>push</c> at an arbitrary point inside a function writes into memory the interrupted function may still be using.
    /// </para>
    /// </summary>
    public static bool HasRedZone { get; } = IsPosix;

    /// <summary>Size of the ABI red zone in bytes; 0 on Windows.</summary>
    public static int RedZoneSize => HasRedZone ? 128 : 0;

    /// <summary>Rounds <paramref name="address"/> down to a page boundary.</summary>
    public static UInt64 AlignDownToPage(UInt64 address) => address & ~((UInt64)PageSize - 1);

    /// <summary>
    /// Number of bytes from the start of the page containing <paramref name="address"/> through the end of the page containing the last byte of the range.
    /// </summary>
    public static UIntPtr PageSpan(UInt64 address, int length)
    {
        UInt64 start = AlignDownToPage(address);
        UInt64 end = (address + (UInt64)length + (UInt64)PageSize - 1) & ~((UInt64)PageSize - 1);
        return (UIntPtr)(end - start);
    }
}
