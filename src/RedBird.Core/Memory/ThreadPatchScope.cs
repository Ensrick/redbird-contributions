using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using RedBird.Core.Platform;

namespace RedBird.Core.Memory;

/// <summary>Result of one protected executable-code patch.</summary>
internal readonly record struct ThreadPatchResult(int SuspendedThreadCount, int RelocatedThreadCount);

/// <summary>
/// Serializes a code patch, suspends every peer thread, redirects instruction pointers in the replaced prologue, writes the patch, and resumes every peer before returning to managed code.
/// </summary>
/// <remarks>
/// The Win32 suspend/context/write/resume interval is one deliberately non-optimized method containing no managed calls or allocations.
/// Linux x86-64 delegates its corresponding dangerous interval to a dependency-free native helper; a managed signal callback is deliberately not used because arbitrary reverse P/Invoke while the CLR is itself being suspended is not safe.
/// Returning to ordinary managed code while CLR worker threads are suspended can deadlock in tiering, GC, or other runtime helpers, even when the apparent managed operation is only <c>List.Count</c>.
/// </remarks>
internal static unsafe class ThreadPatchScope
{
    private const uint SnapshotThreads = 0x00000004;
    private const uint ThreadSuspendResume = 0x0002;
    private const uint ThreadGetContext = 0x0008;
    private const uint ThreadSetContext = 0x0010;
    private const uint ThreadQueryLimitedInformation = 0x0800;
    private const uint InvalidSuspendCount = UInt32.MaxValue;
    private const uint StillActive = 259;
    private const uint PageExecuteReadWriteNoCfgUpdate = 0x40000040;
    private const uint ContextI386Control = 0x00010001;
    private const uint ContextAmd64Control = 0x00100001;
    private const int X86ContextSize = 716;
    private const int X86ContextFlagsOffset = 0;
    private const int X86InstructionPointerOffset = 184;
    private const int X64ContextSize = 1232;
    private const int X64ContextFlagsOffset = 48;
    private const int X64InstructionPointerOffset = 248;
    private const int LinuxSuspendTimeoutMilliseconds = 5000;
    private const long NoGcBudget = 1024 * 1024;

    private static readonly object PatchGate = new();

    public static ThreadPatchResult Apply(InstructionPointerMap map, bool installing, UInt64 patchAddress, byte[] patchBytes)
    {
        if (map is null)
            throw new ArgumentNullException(nameof(map));
        if (patchAddress == 0)
            throw new ArgumentOutOfRangeException(nameof(patchAddress));
        if (patchBytes is null || patchBytes.Length == 0)
            throw new ArgumentException("A protected patch cannot be empty.", nameof(patchBytes));
        if (PlatformInfo.IsLinux)
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("Linux peer-thread suspension around code patches requires an x86-64 process.");

            return ApplyLinux(map, installing, patchAddress, patchBytes);
        }
        if (!PlatformInfo.IsWindows)
            throw new PlatformNotSupportedException("Peer-thread suspension around code patches is supported only on Windows and Linux x86-64.");
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.X86))
            throw new PlatformNotSupportedException("Thread patch safety supports x86 and x64 processes only.");

        Monitor.Enter(PatchGate);
        IntPtr[]? handles = null;
        bool ownsNoGcRegion = false;
        CriticalResult result = default;

        try
        {
            MemoryProtectionRegion[] protectionRegions = MemoryProtectionScope.CaptureWindowsRegions(patchAddress, patchBytes.Length);
            handles = [.. OpenPeerThreads()];
            IntPtr[] suspendedHandles = new IntPtr[handles.Length];

            ownsNoGcRegion = TryEnterNoGcRegionForPatch();

            fixed (IntPtr* handlePointer = handles)
            fixed (IntPtr* suspendedPointer = suspendedHandles)
            fixed (UInt64* sourcePointer = map.SourceInstructionPointers)
            fixed (UInt64* relocatedPointer = map.RelocatedInstructionPointers)
            fixed (byte* patchPointer = patchBytes)
            fixed (MemoryProtectionRegion* protectionPointer = protectionRegions)
            {
                result = ApplyCritical(
                    handlePointer,
                    handles.Length,
                    suspendedPointer,
                    sourcePointer,
                    relocatedPointer,
                    map.SourceInstructionPointers.Length,
                    map.SourceAddress,
                    map.SourceLength,
                    installing,
                    patchAddress,
                    patchPointer,
                    patchBytes.Length,
                    protectionPointer,
                    protectionRegions.Length,
                    IntPtr.Size == 8);
            }
        }
        finally
        {
            if (ownsNoGcRegion)
                EndNoGcRegionAfterCleanup();
            if (handles is not null)
                CloseAll(handles);
            Monitor.Exit(PatchGate);
        }

        ThrowIfCriticalFailed(result);
        return new ThreadPatchResult(result.SuspendedCount, result.RelocatedCount);
    }

    /// <summary>
    /// Pins all managed inputs and invokes the native signal/futex helper. No managed code runs after the first peer receives the patch signal until every signal handler has been released.
    /// </summary>
    private static ThreadPatchResult ApplyLinux(InstructionPointerMap map, bool installing, UInt64 patchAddress, byte[] patchBytes)
    {
        Monitor.Enter(PatchGate);
        bool ownsNoGcRegion = false;
        LinuxCriticalResult result = default;

        try
        {
            MemoryProtectionRegion[] protectionRegions = MemoryProtectionScope.CaptureRegions((IntPtr)patchAddress, patchBytes.Length);

            ownsNoGcRegion = TryEnterNoGcRegionForPatch();

            fixed (UInt64* sourcePointer = map.SourceInstructionPointers)
            fixed (UInt64* relocatedPointer = map.RelocatedInstructionPointers)
            fixed (byte* patchPointer = patchBytes)
            fixed (MemoryProtectionRegion* protectionPointer = protectionRegions)
            {
                int nativeResult = LinuxThreadMethods.Apply(
                    sourcePointer,
                    relocatedPointer,
                    map.SourceInstructionPointers.Length,
                    map.SourceAddress,
                    map.SourceLength,
                    installing ? 1 : 0,
                    patchAddress,
                    patchPointer,
                    patchBytes.Length,
                    protectionPointer,
                    protectionRegions.Length,
                    LinuxSuspendTimeoutMilliseconds,
                    &result);

                if (nativeResult != 0 && result.ErrorStage == 0)
                    result.ErrorStage = 9;
            }
        }
        catch (DllNotFoundException exception)
        {
            throw new PlatformNotSupportedException("The Linux x86-64 thread-patch companion (libredbird_thread_patch.so) could not be loaded. Ensure the linux-x64 native runtime asset is deployed with RedBird.Core.", exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new PlatformNotSupportedException("The deployed Linux thread-patch companion is incompatible with this RedBird.Core version.", exception);
        }
        finally
        {
            if (ownsNoGcRegion)
                EndNoGcRegionAfterCleanup();
            Monitor.Exit(PatchGate);
        }

        ThrowIfLinuxCriticalFailed(result);
        return new ThreadPatchResult(result.SuspendedCount, result.RelocatedCount);
    }

    /// <summary>
    /// The complete dangerous interval. Do not introduce managed calls, managed references, LINQ, exceptions, logging, or collection access here.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static CriticalResult ApplyCritical(
        IntPtr* handles,
        int handleCount,
        IntPtr* suspendedHandles,
        UInt64* sourceInstructionPointers,
        UInt64* relocatedInstructionPointers,
        int instructionCount,
        UInt64 sourceAddress,
        int sourceLength,
        bool installing,
        UInt64 patchAddress,
        byte* patchBytes,
        int patchLength,
        MemoryProtectionRegion* protectionRegions,
        int protectionRegionCount,
        bool is64Bit)
    {
        CriticalResult result = default;
        byte* rawContext = stackalloc byte[X64ContextSize + 15];
        byte* context = is64Bit
            ? (byte*)(((nuint)rawContext + 15) & ~(nuint)15)
            : rawContext;

        for (int handleIndex = 0; handleIndex < handleCount; handleIndex++)
        {
            IntPtr thread = handles[handleIndex];
            if (WindowsThreadMethods.SuspendThread(thread) == InvalidSuspendCount)
            {
                uint suspendError = WindowsThreadMethods.GetLastError();
                uint exitCode = StillActive;
                if (WindowsThreadMethods.GetExitCodeThread(thread, &exitCode) != 0 && exitCode != StillActive)
                    continue;

                result.ErrorStage = 1;
                result.Win32Error = suspendError;
                break;
            }

            suspendedHandles[result.SuspendedCount++] = thread;

            int contextSize = is64Bit ? X64ContextSize : X86ContextSize;
            for (int byteIndex = 0; byteIndex < contextSize; byteIndex++)
                context[byteIndex] = 0;

            if (is64Bit)
                *(uint*)(context + X64ContextFlagsOffset) = ContextAmd64Control;
            else
                *(uint*)(context + X86ContextFlagsOffset) = ContextI386Control;

            if (WindowsThreadMethods.GetThreadContext(thread, context) == 0)
            {
                uint contextError = WindowsThreadMethods.GetLastError();
                uint exitCode = StillActive;
                if (WindowsThreadMethods.GetExitCodeThread(thread, &exitCode) != 0 && exitCode != StillActive)
                {
                    result.SuspendedCount--;
                    continue;
                }

                result.ErrorStage = 2;
                result.Win32Error = contextError;
                break;
            }

            UInt64 instructionPointer = is64Bit
                ? *(UInt64*)(context + X64InstructionPointerOffset)
                : *(uint*)(context + X86InstructionPointerOffset);

            if (instructionPointer < sourceAddress ||
                instructionPointer - sourceAddress >= (UInt64)sourceLength)
                continue;

            int boundaryIndex = -1;
            for (int instructionIndex = 0; instructionIndex < instructionCount; instructionIndex++)
            {
                if (sourceInstructionPointers[instructionIndex] == instructionPointer)
                {
                    boundaryIndex = instructionIndex;
                    break;
                }
            }

            if (boundaryIndex < 0)
            {
                result.ErrorStage = installing ? 3 : 5;
                result.ErrorAddress = instructionPointer;
                break;
            }

            if (!installing)
                continue;

            UInt64 relocated = relocatedInstructionPointers[boundaryIndex];
            if (relocated == 0)
            {
                result.ErrorStage = 3;
                result.ErrorAddress = instructionPointer;
                break;
            }

            if (is64Bit)
                *(UInt64*)(context + X64InstructionPointerOffset) = relocated;
            else
                *(uint*)(context + X86InstructionPointerOffset) = (uint)relocated;

            if (WindowsThreadMethods.SetThreadContext(thread, context) == 0)
            {
                result.ErrorStage = 4;
                result.Win32Error = WindowsThreadMethods.GetLastError();
                result.ErrorAddress = instructionPointer;
                break;
            }

            result.RelocatedCount++;
        }

        if (result.ErrorStage == 0)
        {
            int changedProtectionCount = 0;
            for (; changedProtectionCount < protectionRegionCount; changedProtectionCount++)
            {
                MemoryProtectionRegion* region = protectionRegions + changedProtectionCount;
                uint ignoredProtection = 0;
                int changed = WindowsThreadMethods.VirtualProtect((IntPtr)region->Address, (UIntPtr)region->Length, PageExecuteReadWriteNoCfgUpdate, &ignoredProtection);

                // Pre-CFG Windows kernels reject PAGE_TARGETS_NO_UPDATE. They have no CFG target metadata to preserve, so retry the ordinary RWX transition there.
                if (changed == 0)
                {
                    changed = WindowsThreadMethods.VirtualProtect((IntPtr)region->Address, (UIntPtr)region->Length, PageExecuteReadWriteNoCfgUpdate & 0xFF, &ignoredProtection);
                }

                if (changed != 0)
                {
                    continue;
                }

                result.ErrorStage = 6;
                result.Win32Error = WindowsThreadMethods.GetLastError();
                break;
            }

            if (result.ErrorStage == 0)
            {
                for (int byteIndex = 0; byteIndex < patchLength; byteIndex++)
                    ((byte*)patchAddress)[byteIndex] = patchBytes[byteIndex];
            }

            for (int protectionIndex = changedProtectionCount - 1; protectionIndex >= 0; protectionIndex--)
            {
                MemoryProtectionRegion* region = protectionRegions + protectionIndex;
                uint ignoredProtection = 0;
                uint restoreProtection = region->NativeProtection;
                if ((restoreProtection & 0xF0) != 0)
                    restoreProtection |= 0x40000000;

                int restored = WindowsThreadMethods.VirtualProtect((IntPtr)region->Address, (UIntPtr)region->Length, restoreProtection, &ignoredProtection);
                if (restored == 0 && restoreProtection != region->NativeProtection)
                {
                    restored = WindowsThreadMethods.VirtualProtect((IntPtr)region->Address, (UIntPtr)region->Length, region->NativeProtection, &ignoredProtection);
                }

                if (restored == 0 && result.ErrorStage == 0)
                {
                    result.ErrorStage = 8;
                    result.Win32Error = WindowsThreadMethods.GetLastError();
                    result.ErrorAddress = region->Address;
                }
            }
        }

        for (int suspendedIndex = result.SuspendedCount - 1; suspendedIndex >= 0; suspendedIndex--)
        {
            if (WindowsThreadMethods.ResumeThread(suspendedHandles[suspendedIndex]) == InvalidSuspendCount && result.ErrorStage == 0)
            {
                result.ErrorStage = 7;
                result.Win32Error = WindowsThreadMethods.GetLastError();
            }
        }

        return result;
    }

    private static List<IntPtr> OpenPeerThreads()
    {
        uint processId = WindowsThreadMethods.GetCurrentProcessId();
        uint currentThreadId = WindowsThreadMethods.GetCurrentThreadId();
        IntPtr snapshot = WindowsThreadMethods.CreateToolhelp32Snapshot(SnapshotThreads, 0);
        if (snapshot == new IntPtr(-1))
            throw LastWin32("Could not enumerate process threads before patching executable code");

        List<IntPtr> handles = [];
        try
        {
            ThreadEntry32 entry = new() { Size = (uint)Marshal.SizeOf<ThreadEntry32>() };
            if (!WindowsThreadMethods.Thread32First(snapshot, ref entry))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 18)
                    return handles;
                throw new Win32Exception(error, "Could not read the process thread snapshot.");
            }

            do
            {
                if (entry.OwnerProcessId != processId || entry.ThreadId == currentThreadId)
                    continue;

                IntPtr thread = WindowsThreadMethods.OpenThread(ThreadSuspendResume | ThreadGetContext | ThreadSetContext | ThreadQueryLimitedInformation, false, entry.ThreadId);
                if (thread == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 87)
                        continue;
                    throw new Win32Exception(error, $"Could not open peer thread {entry.ThreadId} for patch safety.");
                }

                handles.Add(thread);
            }
            while (WindowsThreadMethods.Thread32Next(snapshot, ref entry));

            return handles;
        }
        catch
        {
            CloseAll(handles);
            throw;
        }
        finally
        {
            WindowsThreadMethods.CloseHandle(snapshot);
        }
    }

    private static void ThrowIfCriticalFailed(CriticalResult result)
    {
        if (result.ErrorStage == 0)
            return;

        string message = result.ErrorStage switch
        {
            1 => "Could not suspend a peer thread before patching executable code.",
            2 => "Could not read a suspended peer-thread context.",
            3 => $"A suspended thread is at 0x{result.ErrorAddress:X16} inside the patch range, but the trampoline has no equivalent instruction boundary.",
            4 => $"Could not redirect a peer thread at 0x{result.ErrorAddress:X16} into the trampoline.",
            5 => $"A suspended thread is at 0x{result.ErrorAddress:X16} inside the installed patch, but that address is not an original instruction boundary.",
            6 => "Could not make executable code writable inside the protected patch window.",
            7 => "Could not resume a peer thread after patching executable code.",
            8 => $"Could not restore the original memory protection at 0x{result.ErrorAddress:X16}.",
            _ => "The protected patch operation failed.",
        };

        if (result.Win32Error != 0)
            throw new Win32Exception(unchecked((int)result.Win32Error), message);
        throw new InvalidOperationException(message);
    }

    private static void ThrowIfLinuxCriticalFailed(LinuxCriticalResult result)
    {
        if (result.ErrorStage == 0)
            return;

        string message = result.ErrorStage switch
        {
            3 => $"A suspended thread is at 0x{result.ErrorAddress:X16} inside the patch range, but the trampoline has no equivalent instruction boundary.",
            5 => $"A suspended thread is at 0x{result.ErrorAddress:X16} inside the installed patch, but that address is not an original instruction boundary.",
            6 => "Could not make executable code writable inside the protected Linux patch window.",
            8 => $"Could not restore the original memory protection at 0x{result.ErrorAddress:X16}.",
            9 => $"Could not reserve or use a real-time signal for Linux thread suspension (signal {result.SignalNumber}).",
            10 => $"Linux thread {result.ErrorAddress} blocks the thread-patch signal {result.SignalNumber}; the patch was not written.",
            11 => $"Timed out waiting for Linux peer threads to enter the protected patch window (signal {result.SignalNumber}); the patch was not written.",
            12 => "Could not enumerate Linux peer threads before patching executable code.",
            _ => "The protected Linux patch operation failed.",
        };

        if (result.NativeError != 0)
            message += $" Linux errno: {result.NativeError}.";
        throw new InvalidOperationException(message);
    }

    private static void CloseAll(IEnumerable<IntPtr> handles)
    {
        foreach (IntPtr handle in handles)
        {
            if (handle != IntPtr.Zero)
                WindowsThreadMethods.CloseHandle(handle);
        }
    }

    private static void EndNoGcRegionAfterCleanup()
    {
        try
        {
            GC.EndNoGCRegion();
        }
        catch (InvalidOperationException)
        {
            // The runtime ends no-GC mode itself if its budget is exceeded. All peers have already
            // been resumed, so cleanup must not make a successfully applied patch look uninstalled.
        }
    }

    /// <summary>
    /// Enters no-GC mode when the runtime implements it. Unity's Mono exposes the API but throws <see cref="NotImplementedException"/>; in that case the allocation-free critical patch
    /// routine remains usable and the operation proceeds without claiming to own a no-GC region.
    /// </summary>
    /// <remarks>
    /// A normal <see langword="false"/> result is still a hard failure: that means the runtime implements no-GC regions but could not reserve the requested budget. 
    /// Only runtimes which explicitly report the feature as unsupported take the compatibility path.
    /// </remarks>
    internal static bool TryEnterNoGcRegionForPatch(Func<long, bool>? tryStart = null)
    {
        try
        {
            if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
                return false;

            bool started = tryStart is null ? GC.TryStartNoGCRegion(NoGcBudget) : tryStart(NoGcBudget);
            if (!started)
                throw new InvalidOperationException("Could not enter a no-GC region before suspending peer threads.");

            return true;
        }
        catch (NotImplementedException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static Win32Exception LastWin32(string message)
        => new(Marshal.GetLastWin32Error(), message);

    private struct CriticalResult
    {
        public int SuspendedCount;
        public int RelocatedCount;
        public int ErrorStage;
        public uint Win32Error;
        public UInt64 ErrorAddress;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxCriticalResult
    {
        public int SuspendedCount;
        public int RelocatedCount;
        public int ErrorStage;
        public int NativeError;
        public int SignalNumber;
        public int Reserved;
        public UInt64 ErrorAddress;
    }

    private static class LinuxThreadMethods
    {
        [DllImport("redbird_thread_patch", EntryPoint = "rb_patch_apply", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Apply(
            UInt64* sourceInstructionPointers,
            UInt64* relocatedInstructionPointers,
            int instructionCount,
            UInt64 sourceAddress,
            int sourceLength,
            int installing,
            UInt64 patchAddress,
            byte* patchBytes,
            int patchLength,
            MemoryProtectionRegion* protectionRegions,
            int protectionRegionCount,
            int timeoutMilliseconds,
            LinuxCriticalResult* result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ThreadId;
        public uint OwnerProcessId;
        public int BasePriority;
        public int DeltaPriority;
        public uint Flags;
    }

    private static class WindowsThreadMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Thread32First(IntPtr snapshot, ref ThreadEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Thread32Next(IntPtr snapshot, ref ThreadEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
#if NET5_0_OR_GREATER
        [SuppressGCTransition]
#endif
        internal static extern uint SuspendThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
#if NET5_0_OR_GREATER
        [SuppressGCTransition]
#endif
        internal static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
#if NET5_0_OR_GREATER
        [SuppressGCTransition]
#endif
        internal static extern int GetExitCodeThread(IntPtr thread, uint* exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
#if NET5_0_OR_GREATER
        [SuppressGCTransition]
#endif
        internal static extern int GetThreadContext(IntPtr thread, void* context);

        [DllImport("kernel32.dll", SetLastError = true)]
#if NET5_0_OR_GREATER
        [SuppressGCTransition]
#endif
        internal static extern int SetThreadContext(IntPtr thread, void* context);

        [DllImport("kernel32.dll", SetLastError = true)]
#if NET5_0_OR_GREATER
        [SuppressGCTransition]
#endif
        internal static extern int VirtualProtect(IntPtr address, UIntPtr size, uint newProtection, uint* oldProtection);

        [DllImport("kernel32.dll")]
#if NET5_0_OR_GREATER
        [SuppressGCTransition]
#endif
        internal static extern uint GetLastError();

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
