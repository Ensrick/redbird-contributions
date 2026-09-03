using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using Iced.Intel;
using Xunit;
using RedBird.Backends.NativeX64;
using RedBird.Core.Platform;
using RedBird.X64.Tests.Infrastructure;

namespace RedBird.X64.Tests;

/// <summary>Exercises the protected patch window with a thread inside the bytes being replaced.</summary>
public sealed class ThreadSafePatchingTests
{
    [Fact]
    public void Enable_RelocatesAThreadStoppedInsideTheDisplacedPrologue()
    {
        Assert.SkipUnless(
            PlatformInfo.IsWindows ||
            (PlatformInfo.IsLinux && ProcMaps.IsAvailable && RuntimeInformation.ProcessArchitecture == Architecture.X64),
            "Protected peer-thread patching requires Windows x86/x64 or Linux x64.");

        using ExecutableFunction target = CreateBlockingTarget();
        Assert.SkipUnless(target.IsWritable, "The test needs to release the target through its data slot.");

        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();
        using NativeDetour<TestDelegates.NullaryInt> detour = CreateDetour(
            target,
            () => 99,
            suspendThreads: true);

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
                    () => Marshal.ReadInt32(new IntPtr(unchecked((long)target.DataAddress + 4))) > 0,
                    TimeSpan.FromSeconds(5)),
                "The worker did not enter the target prologue.");

            detour.Enable();

            Assert.True(detour.LastPatchSuspendedThreadCount > 0);
            Assert.Equal(1, detour.LastPatchRelocatedThreadCount);

            Marshal.WriteInt32(new IntPtr(unchecked((long)target.DataAddress)), 1);
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)), "The relocated worker did not finish.");
            workerFailure?.Throw();

            // The in-flight call completes the relocated original; new calls take the detour.
            Assert.Equal(7, workerResult);
            Assert.Equal(99, call());

            detour.Disable();
            Assert.Equal(7, call());
        }
        finally
        {
            Marshal.WriteInt32(new IntPtr(unchecked((long)target.DataAddress)), 1);
            worker.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void SuspensionCanBeDisabledForPatchPerformance()
    {
        using ExecutableFunction target = TestFunctions.Constant(7);
        TestDelegates.NullaryInt call = target.GetDelegate<TestDelegates.NullaryInt>();
        using NativeDetour<TestDelegates.NullaryInt> detour = CreateDetour(
            target,
            () => 99,
            suspendThreads: false);

        detour.Enable();

        Assert.Equal(99, call());
        Assert.Equal(0, detour.LastPatchSuspendedThreadCount);
        Assert.Equal(0, detour.LastPatchRelocatedThreadCount);
    }

    private static NativeDetour<TestDelegates.NullaryInt> CreateDetour(
        ExecutableFunction target,
        TestDelegates.NullaryInt callback,
        bool suspendThreads)
    {
        NativeDetourBackend backend = new(new NativeDetourOptions
        {
            AllowedSchemes = DetourScheme.Indirect,
            SuspendThreadsDuringPatch = suspendThreads,
        });

        return (NativeDetour<TestDelegates.NullaryInt>)backend.CreateDetour(
            BackendCatalog.Request(target.Address, callback, "thread-safe-patch"));
    }

    private static ExecutableFunction CreateBlockingTarget()
        => ExecutableFunction.Create(
            (assembler, layout) =>
            {
                Label loop = assembler.CreateLabel();
                assembler.Label(ref loop);

                // Both memory operations are in the displaced prologue. The backward branch makes the selector widen the prologue so the worker can only be suspended at a mapped instruction boundary while the gate remains zero.
                assembler.AddInstruction(Instruction.Create(
                    Code.Inc_rm32,
                    new MemoryOperand(Register.RIP, (long)layout.DataAddress + 4)));
                assembler.AddInstruction(Instruction.Create(
                    Code.Mov_r32_rm32,
                    Register.EAX,
                    new MemoryOperand(Register.RIP, (long)layout.DataAddress)));
                assembler.test(AssemblerRegisters.eax, AssemblerRegisters.eax);
                assembler.je(loop);
                assembler.mov(AssemblerRegisters.eax, 7);
                assembler.ret();
            },
            layout =>
            {
                layout.SetData(0);
                Marshal.WriteInt32(new IntPtr(unchecked((long)layout.DataAddress + 4)), 0);
            });
}
