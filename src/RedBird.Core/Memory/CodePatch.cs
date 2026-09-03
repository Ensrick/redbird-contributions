using System;
using System.Runtime.InteropServices;
using RedBird.Core.Platform;

namespace RedBird.Core.Memory;

/// <summary>
/// Reads and writes executable memory: unprotect, write, reprotect, flush the instruction cache.
/// </summary>
public static unsafe class CodePatch
{
    /// <summary>Size of the position-independent absolute jump produced by <see cref="BuildAbsoluteJump"/>.</summary>
    public const int AbsoluteJumpSize = 14;

    /// <summary>Snapshots <paramref name="length"/> bytes at <paramref name="address"/>.</summary>
    public static byte[] Read(UInt64 address, int length)
    {
        if (address == 0)
            throw new ArgumentOutOfRangeException(nameof(address));
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        byte[] buffer = new byte[length];
        Marshal.Copy((IntPtr)address, buffer, 0, length);
        return buffer;
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> over executable code, restoring the original page protection afterwards and flushing the instruction cache where the architecture needs it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The page could not be made writable.</exception>
    public static void Write(UInt64 address, ReadOnlySpan<byte> bytes)
    {
        if (address == 0)
            throw new ArgumentOutOfRangeException(nameof(address));
        if (bytes.IsEmpty)
            return;

        if (!TryUnprotect(address, bytes.Length, out MemoryProtectionScope? protectionScope) ||
            protectionScope is null)
        {
            throw new InvalidOperationException($"Failed to make 0x{address:X16} ({bytes.Length} bytes) writable. {(PlatformInfo.IsPosix ? " On hardened kernels (SELinux deny_execmem, PaX MPROTECT) a mapping cannot be writable and executable at once; this call already retried with a read-write transition." : string.Empty) }");
        }

        try
        {
            fixed (byte* source = bytes)
            {
                Buffer.MemoryCopy(source, (void*)address, bytes.Length, bytes.Length);
            }
        }
        finally
        {
            // Always restore protection, even if the copy threw. Leaving a code page writable is both a hazard and a red flag for anti-cheat and EDR.
            protectionScope.Dispose();
        }

        Flush(address, bytes.Length);
    }

    /// <summary>
    /// Makes a code range writable, preferring a transition that keeps it executable.
    /// </summary>
    /// <remarks>
    /// Execute-read-write is tried first: on Windows it is the normal thing to ask for, and on POSIX it keeps the page executable throughout, so a thread running in the patched function does not fault mid-write. 
    /// Where a W^X policy refuses that combination, the fallback drops execute for the duration of the write, which is safe only if no other thread is executing
    /// there, and is the reason the install phase of a transaction is the right place to suspend threads.
    /// </remarks>
    private static bool TryUnprotect(UInt64 address, int length, out MemoryProtectionScope? protectionScope)
    {
        if (MemoryProtectionScope.TryAcquire((IntPtr)address, length, MemoryProtection.ExecuteReadWrite, out protectionScope))
            return true;

        if (!PlatformInfo.IsPosix)
            return false;

        return MemoryProtectionScope.TryAcquire((IntPtr)address, length, MemoryProtection.ReadWrite, out protectionScope);
    }

    /// <summary>
    /// Flushes the instruction cache for the range. A no-op on x86 and x86-64, where the caches are architecturally coherent, see <see cref="InstructionCache"/>.
    /// </summary>
    public static void Flush(UInt64 address, int length) => InstructionCache.Flush(address, length);

    /// <summary>
    /// Builds the 14-byte position-independent absolute jump
    /// <c>FF 25 00 00 00 00 &lt;8-byte target&gt;</c> (<c>jmp qword ptr [rip+0]</c>), optionally
    /// padded with <c>NOP</c> to <paramref name="totalLength"/>.
    /// </summary>
    public static byte[] BuildAbsoluteJump(UInt64 target, int totalLength = AbsoluteJumpSize)
    {
        if (totalLength < AbsoluteJumpSize)
            throw new ArgumentOutOfRangeException(nameof(totalLength), $"An absolute jump needs at least {AbsoluteJumpSize} bytes.");

        byte[] jump = new byte[totalLength];
        jump[0] = 0xFF;
        jump[1] = 0x25;
        // jump[2..5] stay zero: the RIP-relative displacement is 0, so the pointer follows inline.

        fixed (byte* p = jump)
        {
            *(UInt64*)(p + 6) = target;
        }

        for (int i = AbsoluteJumpSize; i < totalLength; i++)
            jump[i] = 0x90;

        return jump;
    }
}
