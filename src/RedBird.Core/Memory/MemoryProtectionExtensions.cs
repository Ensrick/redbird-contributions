using RedBird.Core.PInvoke;

namespace RedBird.Core.Memory;

/// <summary>
/// Helpers that translate the cross-platform <see cref="MemoryProtection"/> enum into the flag types expected by each supported platform's native memory APIs.
/// </summary>
internal static class MemoryProtectionExtensions
{
    /// <summary>
    /// Translates a <see cref="MemoryProtection"/> value into the closest matching
    /// Win32 <see cref="NativeMethods.Win32MemoryPermissions"/> PAGE_* constant.
    /// </summary>
    internal static NativeMethods.Win32MemoryPermissions ToWindowsProtection(this MemoryProtection protection)
        => (NativeMethods.Win32MemoryPermissions)MemoryProtectionConverter.ToWin32(protection);

    /// <summary>
    /// Translates a <see cref="MemoryProtection"/> value into a POSIX <c>mmap</c>/<c>mprotect</c> PROT_* bitmask (see <see cref="NativeMethods.PROT_READ"/>, <see cref="NativeMethods.PROT_WRITE"/>, <see cref="NativeMethods.PROT_EXEC"/>).
    /// </summary>
    internal static int ToPosixProtection(this MemoryProtection protection)
    {
        int prot = 0; // PROT_NONE
        if (protection.HasFlag(MemoryProtection.Read)) prot |= NativeMethods.PROT_READ;
        // POSIX has no copy-on-write protection flag; treat WriteCopy as a plain Write request.
        if (protection.HasFlag(MemoryProtection.Write) || protection.HasFlag(MemoryProtection.WriteCopy)) prot |= NativeMethods.PROT_WRITE;
        if (protection.HasFlag(MemoryProtection.Execute)) prot |= NativeMethods.PROT_EXEC;
        return prot;
    }
}
