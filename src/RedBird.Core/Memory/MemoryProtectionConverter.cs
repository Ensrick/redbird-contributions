using RedBird.Core.PInvoke;

namespace RedBird.Core.Memory;

internal static class MemoryProtectionConverter
{
    private const MemoryProtection ModifierMask =
        MemoryProtection.Guard |
        MemoryProtection.NoCache |
        MemoryProtection.WriteCombine |
        MemoryProtection.ControlFlowGuard;

    /// <summary>Converts a <see cref="MemoryProtection"/> value to a Win32 PAGE_* constant.</summary>
    public static uint ToWin32(MemoryProtection protection)
    {
        uint win32 = (protection & ~ModifierMask) switch
        {
            MemoryProtection.NoAccess => 0x01,         // PAGE_NOACCESS
            MemoryProtection.Read => 0x02,             // PAGE_READONLY
            MemoryProtection.ReadWrite => 0x04,        // PAGE_READWRITE
            MemoryProtection.WriteCopy => 0x08,        // PAGE_WRITECOPY
            MemoryProtection.Execute => 0x10,          // PAGE_EXECUTE
            MemoryProtection.ExecuteRead => 0x20,      // PAGE_EXECUTE_READ
            MemoryProtection.ExecuteReadWrite => 0x40, // PAGE_EXECUTE_READWRITE
            MemoryProtection.ExecuteWriteCopy => 0x80, // PAGE_EXECUTE_WRITECOPY
            _ => 0x01,
        };

        if ((protection & MemoryProtection.Guard) != 0) win32 |= 0x100;
        if ((protection & MemoryProtection.NoCache) != 0) win32 |= 0x200;
        if ((protection & MemoryProtection.WriteCombine) != 0) win32 |= 0x400;
        if ((protection & MemoryProtection.ControlFlowGuard) != 0) win32 |= 0x40000000;
        return win32;
    }

    /// <summary>Converts a Win32 PAGE_* constant to a <see cref="MemoryProtection"/> value.</summary>
    public static MemoryProtection FromWin32(uint win32Protect)
    {
        uint baseProtection = win32Protect & 0xFF;
        MemoryProtection protection = baseProtection switch
        {
            0x01 => MemoryProtection.NoAccess,
            0x02 => MemoryProtection.Read,
            0x04 => MemoryProtection.ReadWrite,
            0x08 => MemoryProtection.WriteCopy,
            0x10 => MemoryProtection.Execute,
            0x20 => MemoryProtection.ExecuteRead,
            0x40 => MemoryProtection.ExecuteReadWrite,
            0x80 => MemoryProtection.ExecuteWriteCopy,
            _ => MemoryProtection.NoAccess,
        };

        if ((win32Protect & 0x100) != 0) protection |= MemoryProtection.Guard;
        if ((win32Protect & 0x200) != 0) protection |= MemoryProtection.NoCache;
        if ((win32Protect & 0x400) != 0) protection |= MemoryProtection.WriteCombine;
        if ((win32Protect & 0x40000000) != 0) protection |= MemoryProtection.ControlFlowGuard;
        return protection;
    }

    /// <summary>Converts a <see cref="MemoryProtection"/> value to POSIX PROT_* flags.</summary>
    public static int ToPosix(MemoryProtection protection)
    {
        if (protection == MemoryProtection.NoAccess)
            return PosixNativeMethods.PROT_NONE;

        int flags = 0;

        if ((protection & MemoryProtection.Read) != 0)
            flags |= PosixNativeMethods.PROT_READ;

        // Windows copy-on-write has no POSIX protection bit, it is a mapping property (MAP_PRIVATE), which every mapping this library creates already uses.
        // Treating it as writable is the closest equivalent.
        if ((protection & (MemoryProtection.Write | MemoryProtection.WriteCopy)) != 0)
            flags |= PosixNativeMethods.PROT_WRITE;

        if ((protection & MemoryProtection.Execute) != 0)
            flags |= PosixNativeMethods.PROT_EXEC;

        return flags;
    }

    /// <summary>
    /// Converts POSIX PROT_* flags back to a <see cref="MemoryProtection"/> value. 
    /// Needed because <c>mprotect</c>, unlike <c>VirtualProtect</c>, does not report the previous protection.
    /// </summary>
    public static MemoryProtection FromPosix(int flags)
    {
        MemoryProtection protection = MemoryProtection.NoAccess;

        if ((flags & PosixNativeMethods.PROT_READ) != 0)
            protection |= MemoryProtection.Read;
        if ((flags & PosixNativeMethods.PROT_WRITE) != 0)
            protection |= MemoryProtection.Write;
        if ((flags & PosixNativeMethods.PROT_EXEC) != 0)
            protection |= MemoryProtection.Execute;

        return protection;
    }
}
