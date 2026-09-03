using System;
using Iced.Intel;
using Xunit;
using RedBird.X64.Tests.Infrastructure;

namespace RedBird.X64.Tests;

/// <summary>Tests the public runtime-assembled function as a standalone feature.</summary>
public sealed class ExecutableFunctionTests
{
    [Fact]
    public void Create_ProducesCallableCodeAndAnAccurateSnapshot()
    {
        using ExecutableFunction function = ExecutableFunction.Create(assembler =>
        {
            assembler.mov(AssemblerRegisters.eax, AbiRegisters.Arg0D);
            assembler.add(AssemblerRegisters.eax, AbiRegisters.Arg1D);
            assembler.ret();
        });

        TestDelegates.BinaryInt call = function.GetDelegate<TestDelegates.BinaryInt>();

        Assert.Equal(7, call(3, 4));
        Assert.NotEqual(0UL, function.Address);
        Assert.Equal(new IntPtr(unchecked((long)function.Address)), function.EntryPoint);
        Assert.Equal(function.OriginalBytes.Length, function.CodeLength);
        Assert.Equal(function.OriginalBytes, function.ReadCurrentBytes(function.CodeLength));
        Assert.True(function.MatchesOriginal(function.CodeLength));
    }

    [Fact]
    public void Layout_DataSlotCanSeedRipRelativeCode()
    {
        using ExecutableFunction function = ExecutableFunction.Create(
            (assembler, layout) =>
            {
                assembler.AddInstruction(Instruction.Create(
                    Code.Mov_r32_rm32,
                    Register.EAX,
                    new MemoryOperand(Register.RIP, (long)layout.DataAddress)));
                assembler.ret();
            },
            layout => layout.SetData(123456));

        Assert.Equal(function.Address + 2048, function.DataAddress);
        Assert.Equal(123456, function.DataValue);
        Assert.Equal(123456, function.GetDelegate<TestDelegates.NullaryInt>()());
    }

    [Fact]
    public void DisposedFunctionRejectsFurtherAccess()
    {
        ExecutableFunction function = ExecutableFunction.Create(assembler => assembler.ret());
        function.Dispose();

        Assert.Throws<ObjectDisposedException>(() => function.GetDelegate<TestDelegates.NullaryInt>());
        Assert.Throws<ObjectDisposedException>(() => function.ReadCurrentBytes(1));
        Assert.Throws<ObjectDisposedException>(() => function.DataValue);
    }

    [Fact]
    public void InvalidBuildersFailClearly()
    {
        Assert.Throws<ArgumentNullException>(() => ExecutableFunction.Create((Action<Assembler>)null!));
        Assert.Throws<InvalidOperationException>(() => ExecutableFunction.Create(assembler =>
        {
            for (int index = 0; index < 2049; index++)
                assembler.nop();
        }));
    }
}
