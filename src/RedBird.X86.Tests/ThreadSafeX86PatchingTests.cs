using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using Iced.Intel;
using Xunit;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX86;
using RedBird.Core.Platform;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.X86.Tests;

/// <summary>X86 counterpart of the exact-prologue thread relocation test.</summary>
public sealed class ThreadSafeX86PatchingTests
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NullaryInt();

    [Fact]
    public void Enable_RelocatesAThreadStoppedInsideTheDisplacedPrologue()
    {
        Assert.SkipUnless(PlatformInfo.IsWindows, "Win32 thread contexts are required.");

        using ExecutableX86Function target = CreateBlockingTarget();
        Assert.SkipUnless(target.IsWritable, "The test needs to release the target through its data slot.");
        NullaryInt call = target.GetDelegate<NullaryInt>();

        X86DetourBackend backend = new(new X86DetourOptions { SuspendThreadsDuringPatch = true });
        using X86Detour<NullaryInt> detour = (X86Detour<NullaryInt>)backend.CreateDetour(
            new DetourRequest<NullaryInt>
            {
                Name = "x86-thread-safe-patch",
                TargetAddress = target.Address,
                Callback = () => 99,
            });

        int workerResult = 0;
        ExceptionDispatchInfo? workerFailure = null;
        Thread worker = new(() =>
        {
            try
            {
                workerResult = call();
            }
            catch (Exception exception)
            {
                workerFailure = ExceptionDispatchInfo.Capture(exception);
            }
        });

        worker.Start();
        try
        {
            Assert.True(
                SpinWait.SpinUntil(
                    () => Marshal.ReadInt32(new IntPtr(unchecked((int)target.DataAddress + 4))) > 0,
                    TimeSpan.FromSeconds(5)),
                "The worker did not enter the target prologue.");

            detour.Enable();
            Assert.True(detour.LastPatchSuspendedThreadCount > 0);
            Assert.Equal(1, detour.LastPatchRelocatedThreadCount);

            Marshal.WriteInt32(new IntPtr(unchecked((int)target.DataAddress)), 1);
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)), "The relocated worker did not finish.");
            workerFailure?.Throw();
            Assert.Equal(7, workerResult);
            Assert.Equal(99, call());

            detour.Disable();
            Assert.Equal(7, call());
        }
        finally
        {
            Marshal.WriteInt32(new IntPtr(unchecked((int)target.DataAddress)), 1);
            worker.Join(TimeSpan.FromSeconds(5));
        }
    }

    private static ExecutableX86Function CreateBlockingTarget()
        => ExecutableX86Function.Create(
            (assembler, layout) =>
            {
                Label loop = assembler.CreateLabel();
                assembler.Label(ref loop);
                assembler.inc(__dword_ptr[(uint)layout.DataAddress + 4]);
                assembler.mov(eax, __dword_ptr[(uint)layout.DataAddress]);
                assembler.test(eax, eax);
                assembler.je(loop);
                assembler.mov(eax, 7);
                assembler.ret();
            },
            layout =>
            {
                layout.SetData(0);
                Marshal.WriteInt32(new IntPtr(unchecked((int)layout.DataAddress + 4)), 0);
            });
}
