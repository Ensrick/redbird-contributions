using System;
using RedBird.Core.PInvoke;
using RedBird.Core.Platform;

namespace RedBird.Core.Memory;

/// <summary>
/// Makes freshly written code visible to the instruction fetcher.
/// </summary>
public static unsafe class InstructionCache
{
    /// <summary>
    /// Flushes the range if the platform and architecture require it. A no-op where the instruction cache is architecturally coherent.
    /// </summary>
    /// <returns>
    /// False only when a flush was needed but no mechanism was available, on ARM without <c>__clear_cache</c>, where the freshly written code may not be observed.
    /// </returns>
    public static bool Flush(UInt64 address, int length)
    {
        if (length <= 0)
            return true;

        if (PlatformInfo.IsWindows)
        {
            return WindowsMemoryMethods.FlushInstructionCache(CurrentProcessHandle, (IntPtr)address, (UIntPtr)length);
        }

        if (PlatformInfo.HasCoherentInstructionCache)
            return true;

        return PosixNativeMethods.TryClearCache((IntPtr)address, (IntPtr)(address + (UInt64)length));
    }

    /// <summary>
    /// Documented Win32 pseudo-handle for the current process.
    /// </summary>
    public static IntPtr CurrentProcessHandle => new(-1);
}
