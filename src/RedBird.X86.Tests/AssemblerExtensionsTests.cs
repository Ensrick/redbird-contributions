using System;
using System.Runtime.InteropServices;
using Iced.Intel;
using RedBird.X86.Extensions;
using Xunit;
using X86ArgumentSourceType = RedBird.X86.Extensions.AssemblerExtensions.X86ArgumentSourceType;
using X86StackArgument = RedBird.X86.Extensions.AssemblerExtensions.X86StackArgument;
using static Iced.Intel.AssemblerRegisters;
using RedBird.Abstractions.CallingConventions;

namespace RedBird.X86.Tests;

public sealed class AssemblerExtensionsTests
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NullaryInt();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BinaryInt(int first, int second);

    [Fact]
    public void CdeclSafe_ForwardsOriginalStackArguments()
    {
        Assert.Equal(Architecture.X86, RuntimeInformation.ProcessArchitecture);

        using ExecutableX86Function target = ExecutableX86Function.Create(assembler =>
        {
            assembler.mov(eax, __dword_ptr[esp + 4]);
            assembler.add(eax, __dword_ptr[esp + 8]);
            assembler.ret();
        });

        using ExecutableX86Function callSite = ExecutableX86Function.Create(assembler =>
        {
            assembler.X86CdeclSafe(
                target.Address,
                preserveEAX: false,
                stackArguments:
                [
                    new X86StackArgument(X86ArgumentSourceType.FromOriginalStack, 0),
                    new X86StackArgument(X86ArgumentSourceType.FromOriginalStack, 1),
                ]);
            assembler.ret();
        });

        Assert.Equal(7, callSite.GetDelegate<BinaryInt>()(3, 4));
    }

    [Fact]
    public void StdcallSafe_HandlesCalleeCleanupAndImmediateArguments()
    {
        Assert.Equal(Architecture.X86, RuntimeInformation.ProcessArchitecture);

        using ExecutableX86Function target = ExecutableX86Function.Create(assembler =>
        {
            assembler.mov(eax, __dword_ptr[esp + 4]);
            assembler.add(eax, __dword_ptr[esp + 8]);
            assembler.ret(8);
        });

        using ExecutableX86Function callSite = ExecutableX86Function.Create(assembler =>
        {
            assembler.X86StdcallSafe(
                target.Address,
                preserveEAX: false,
                stackArguments:
                [
                    new X86StackArgument(X86ArgumentSourceType.FromImmediate, 10),
                    new X86StackArgument(X86ArgumentSourceType.FromImmediate, 20),
                ]);
            assembler.ret();
        });

        Assert.Equal(30, callSite.GetDelegate<NullaryInt>()());
    }

    [Fact]
    public void FastcallSafe_CombinesRegisterAndStackArguments()
    {
        Assert.Equal(Architecture.X86, RuntimeInformation.ProcessArchitecture);

        using ExecutableX86Function target = ExecutableX86Function.Create(assembler =>
        {
            assembler.mov(eax, ecx);
            assembler.add(eax, edx);
            assembler.add(eax, __dword_ptr[esp + 4]);
            assembler.ret(4);
        });

        using ExecutableX86Function callSite = ExecutableX86Function.Create(assembler =>
        {
            assembler.X86FastcallSafe(
                target.Address,
                prepareArgumentsAction: static asm =>
                {
                    asm.mov(ecx, 1);
                    asm.mov(edx, 2);
                },
                preserveEAX: false,
                stackArguments:
                [
                    new X86StackArgument(X86ArgumentSourceType.FromImmediate, 3),
                ]);
            assembler.ret();
        });

        Assert.Equal(6, callSite.GetDelegate<NullaryInt>()());
    }

    [Fact]
    public void CallSafe_RejectsInvalidDescriptorsBeforeEmittingCode()
    {
        Assembler assembler = new(32);

        Assert.Throws<ArgumentOutOfRangeException>(() => assembler.X86CallSafe(
            0x1234,
            (X86CallingConvention)Int32.MaxValue));

        Assert.Empty(assembler.Instructions);
    }
}
