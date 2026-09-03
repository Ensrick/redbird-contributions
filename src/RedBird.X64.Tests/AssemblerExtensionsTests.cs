using System;
using System.IO;
using System.Linq;
using Iced.Intel;
using RedBird.X64.Extensions;
using Xunit;
using X64ArgumentSourceType = RedBird.X64.Extensions.AssemblerExtensions.X64ArgumentSourceType;
using X64StackArgument = RedBird.X64.Extensions.AssemblerExtensions.X64StackArgument;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.X64.Tests;

public sealed class AssemblerExtensionsTests
{
    [Fact]
    public void FastcallSafe_PreservesRbpAndSupportsUnalignedXmmSaves()
    {
        Assembler assembler = new(64);

        assembler.X64FastcallSafe(
            0x1234_5678_9ABC_DEF0,
            totalArgumentCount: 6,
            prepareArgumentsAction: static asm => asm.mov(rcx, rbp),
            preserveRAX: false,
            preserveXMM: true,
            stackArguments:
            [
                new X64StackArgument(X64ArgumentSourceType.FromOriginalStack, 0),
                new X64StackArgument(X64ArgumentSourceType.FromImmediate, 0x1122_3344_5566_7788),
            ]);

        Assert.DoesNotContain(assembler.Instructions, instruction => instruction.Op0Register == Register.RBP);
        Assert.Equal(12, assembler.Instructions.Count(instruction => instruction.Mnemonic == Mnemonic.Movdqu));
        Assert.DoesNotContain(assembler.Instructions, instruction => instruction.Mnemonic == Mnemonic.Movaps);
        Assert.Contains(
            assembler.Instructions,
            instruction => instruction.MemoryBase == Register.R11 && instruction.MemoryDisplacement64 >= 0x28);

        using MemoryStream stream = new();
        Assert.True(
            assembler.TryAssemble(new StreamCodeWriter(stream), 0x0000_7FF0_0000_0000, out string? error, out _),
            error);
    }

    [Fact]
    public void FastcallSafe_RejectsInvalidDescriptorsBeforeEmittingCode()
    {
        Assembler assembler = new(64);

        Assert.Throws<ArgumentException>(() => assembler.X64FastcallSafe(
            0x1234,
            totalArgumentCount: 5));

        Assert.Empty(assembler.Instructions);
    }
}
