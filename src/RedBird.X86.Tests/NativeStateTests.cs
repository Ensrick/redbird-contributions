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

    private delegate int NullaryInt();
}
