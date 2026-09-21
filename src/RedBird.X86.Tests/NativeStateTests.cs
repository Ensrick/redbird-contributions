using Iced.Intel;
using RedBird.Core.Memory;
using RedBird.X86.Extensions;
using RedBird.X86.Tests.Infrastructure;
using Xunit;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.X86.Tests;

public sealed class NativeStateTests
{
    [Fact]
    public void Flag_CanChangeAnAlreadyAssembledFunctionWithoutAManagedCallback()
    {
        using NativeStateBlock state = new();
        NativeFlag enabled = state.AllocateFlag();

        using ExecutableX86Function function = ExecutableX86Function.Create(assembler =>
        {
            Label disabled = assembler.CreateLabel();

            assembler.mov(ecx, 40);
            assembler.TestNativeFlag(enabled, eax);
            assembler.jz(disabled);
            assembler.mov(eax, ecx);
            assembler.ret();

            assembler.Label(ref disabled);
            assembler.mov(eax, ecx);
            assembler.inc(eax);
            assembler.ret();
        });

        NullaryInt call = function.GetDelegate<NullaryInt>();

        Assert.Equal(41, call());
        enabled.Value = true;
        Assert.Equal(40, call());
        enabled.Value = false;
        Assert.Equal(41, call());
    }

    [Fact]
    public void FlagArray_CanUseRuntimeRegisterIndex()
    {
        using NativeStateBlock state = new();
        NativeFlagArray flags = state.AllocateFlags(4);
        flags[1] = true;

        using ExecutableX86Function function = ExecutableX86Function.Create(assembler =>
        {
            Label disabled = assembler.CreateLabel();

            assembler.mov(ecx, __dword_ptr[esp + 4]);
            assembler.mov(edx, 40);
            assembler.TestNativeFlag(flags, ecx, edx);
            assembler.jz(disabled);
            assembler.mov(eax, edx);
            assembler.ret();

            assembler.Label(ref disabled);
            assembler.mov(eax, edx);
            assembler.inc(eax);
            assembler.ret();
        });

        UnaryInt call = function.GetDelegate<UnaryInt>();

        Assert.Equal(41, call(0));
        Assert.Equal(40, call(1));
    }

    [Fact]
    public void FlagArray_CanLoadRuntimeIndexFromStackMemory()
    {
        using NativeStateBlock state = new();
        NativeFlagArray flags = state.AllocateFlags(4);
        flags[2] = true;

        using ExecutableX86Function function = ExecutableX86Function.Create(assembler =>
        {
            Label disabled = assembler.CreateLabel();

            assembler.mov(edx, 40);
            assembler.TestNativeFlag(flags, __dword_ptr[esp + 4], ecx, edx);
            assembler.jz(disabled);
            assembler.mov(eax, edx);
            assembler.ret();

            assembler.Label(ref disabled);
            assembler.mov(eax, edx);
            assembler.inc(eax);
            assembler.ret();
        });

        UnaryInt call = function.GetDelegate<UnaryInt>();

        Assert.Equal(41, call(1));
        Assert.Equal(40, call(2));
    }

    private delegate int NullaryInt();
    private delegate int UnaryInt(int index);
}
