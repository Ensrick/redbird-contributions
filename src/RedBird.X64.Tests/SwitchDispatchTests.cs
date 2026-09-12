using Iced.Intel;
using RedBird.Core.Memory;
using RedBird.X64.Assembly;
using RedBird.X64.Assembly.InstructionWalker;
using RedBird.X64.Assembly.Switches;
using System;
using Xunit;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.X64.Tests;

public sealed class SwitchDispatchTests
{
    [Fact]
    public void Decode_ResolvesAByteMappedUnsignedImageRelativeTableAndItsBoundaries()
    {
        const int mappingOffset = 0x800;
        const int jumpTableOffset = mappingOffset + 0x10;

        using ExecutableFunction function = ExecutableFunction.Create((assembler, layout) =>
        {
            Assert.Equal((UInt64)mappingOffset, layout.DataAddress - layout.CodeAddress);

            Label case0 = assembler.CreateLabel();
            Label case1And2 = assembler.CreateLabel();
            Label case3 = assembler.CreateLabel();
            Label defaultCase = assembler.CreateLabel();

            assembler.sub(eax, 0x65);
            assembler.cmp(eax, 3);
            assembler.ja(defaultCase);
            assembler.AddInstruction(Instruction.Create(
                Code.Lea_r64_m,
                Register.R8,
                new MemoryOperand(Register.RIP, unchecked((long)layout.CodeAddress))));

            // movzx eax, byte ptr [r8 + rax + 0x800]
            assembler.db(new byte[] { 0x41, 0x0F, 0xB6, 0x84, 0x00, 0x00, 0x08, 0x00, 0x00 });
            // mov ecx, dword ptr [r8 + rax*4 + 0x810]
            assembler.db(new byte[] { 0x41, 0x8B, 0x8C, 0x80, 0x10, 0x08, 0x00, 0x00 });
            assembler.add(rcx, r8);
            assembler.jmp(rcx);

            assembler.Label(ref case0);
            assembler.mov(eax, 0x10);
            assembler.nop(); // Deliberate fall-through: the detected next case is the boundary.

            assembler.Label(ref case1And2);
            assembler.mov(eax, 0x11);
            assembler.ret();

            assembler.Label(ref case3);
            assembler.mov(eax, 0x13);
            assembler.ret();

            assembler.Label(ref defaultCase);
            assembler.mov(eax, 0x7F);
            assembler.ret();
        });

        UInt64 case0Address = FindAddress(function, 0xB8, 0x10, 0x00, 0x00, 0x00, 0x90);
        UInt64 case1Address = FindAddress(function, 0xB8, 0x11, 0x00, 0x00, 0x00, 0xC3);
        UInt64 case3Address = FindAddress(function, 0xB8, 0x13, 0x00, 0x00, 0x00, 0xC3);
        UInt64 defaultAddress = FindAddress(function, 0xB8, 0x7F, 0x00, 0x00, 0x00, 0xC3);

        CodePatch.Write(function.Address + mappingOffset, new byte[] { 0, 1, 1, 2 });
        CodePatch.Write(
            function.Address + jumpTableOffset,
            BuildRelative32Table(function.Address, case0Address, case1Address, case3Address));

        using ScanRegion region = ScanRegion.FromPointer(function.EntryPoint, 4096, "mapped-switch");
        Assert.True(
            SwitchDispatchDecoder.TryDecode(region, function.Address, out SwitchDispatch? dispatch, out string? error),
            error);
        Assert.NotNull(dispatch);

        Assert.Equal(SwitchTableLayout.ByteMapped, dispatch.Layout);
        Assert.Equal(SwitchTableEntryEncoding.UnsignedRelative32, dispatch.EntryEncoding);
        Assert.Equal((UInt64?)(function.Address + mappingOffset), dispatch.MappingTableAddress);
        Assert.Equal(function.Address + jumpTableOffset, dispatch.TableAddress);
        Assert.Equal(function.Address, dispatch.EntryBaseAddress);
        Assert.Equal(0x65, dispatch.MinimumCaseValue);
        Assert.Equal(0x68, dispatch.MaximumCaseValue);
        Assert.Equal(4, dispatch.CaseCount);
        Assert.Equal(defaultAddress, dispatch.DefaultTargetAddress);

        SwitchCaseBlock case0 = dispatch.GetCase(0x65);
        SwitchCaseBlock case1 = dispatch.GetCase(0x66);
        Assert.Same(case1, dispatch.GetCase(0x67));
        SwitchCaseBlock case3 = dispatch.GetCase(0x68);

        Assert.Equal(case0Address, case0.StartAddress);
        Assert.Equal(case1Address, case0.EndAddressExclusive);
        Assert.Equal(case3Address, case1.EndAddressExclusive);
        Assert.Equal(defaultAddress, case3.EndAddressExclusive);
        Assert.Equal(new long[] { 0x66, 0x67 }, case1.CaseValues);
        Assert.True(case1.IsShared);
        Assert.True(dispatch.DefaultBlock.IsDefaultTarget);
        Assert.Equal(defaultAddress + 6, dispatch.FunctionEndAddressExclusive);

        InstructionWalker boundedWalker = case0.CreateWalker(new InstructionWalkerConfig
        {
            WalkRules =
            [
                new InstructionWalkRule
                {
                    Predicate = InstructionWalkerHelpers.IsMov,
                    Extractor = InstructionWalkerHelpers.ExtractImmediate,
                },
            ],
        });

        Assert.True(boundedWalker.TryWalk(out UInt32 immediate));
        Assert.Equal(0x10u, immediate);
        Assert.False(boundedWalker.TryWalk(out UInt32 _));
        Assert.Equal(case0.EndAddressExclusive, boundedWalker.Instructions[^1].NextIP);
    }

    [Fact]
    public void Decode_ResolvesADirectSignedTableRelativeLayout()
    {
        using ExecutableFunction function = ExecutableFunction.Create((assembler, layout) =>
        {
            Label case0 = assembler.CreateLabel();
            Label case1 = assembler.CreateLabel();
            Label case2 = assembler.CreateLabel();
            Label defaultCase = assembler.CreateLabel();

            // Clang commonly compares the incoming selector and then copies it into the table
            // index register. The decoder traces that register alias backwards.
            assembler.cmp(edi, 2);
            assembler.ja(defaultCase);
            assembler.mov(eax, edi);
            assembler.AddInstruction(Instruction.Create(
                Code.Lea_r64_m,
                Register.RDX,
                new MemoryOperand(Register.RIP, unchecked((long)layout.DataAddress))));
            // movsxd rax, dword ptr [rdx + rax*4]
            assembler.db(new byte[] { 0x48, 0x63, 0x04, 0x82 });
            assembler.add(rax, rdx);
            assembler.jmp(rax);

            assembler.Label(ref case0);
            assembler.mov(eax, 0x20);
            assembler.ret();

            assembler.Label(ref case1);
            assembler.mov(eax, 0x21);
            assembler.ret();

            assembler.Label(ref case2);
            assembler.mov(eax, 0x22);
            assembler.ret();

            assembler.Label(ref defaultCase);
            assembler.mov(eax, 0x2F);
            assembler.ret();
        });

        UInt64 case0Address = FindAddress(function, 0xB8, 0x20, 0x00, 0x00, 0x00, 0xC3);
        UInt64 case1Address = FindAddress(function, 0xB8, 0x21, 0x00, 0x00, 0x00, 0xC3);
        UInt64 case2Address = FindAddress(function, 0xB8, 0x22, 0x00, 0x00, 0x00, 0xC3);

        CodePatch.Write(
            function.DataAddress,
            BuildRelative32Table(function.DataAddress, case0Address, case1Address, case2Address));

        using ScanRegion region = ScanRegion.FromPointer(function.EntryPoint, 4096, "direct-switch");
        SwitchDispatch dispatch = SwitchDispatchDecoder.Decode(region, function.Address);

        Assert.Equal(SwitchTableLayout.Direct, dispatch.Layout);
        Assert.Equal(SwitchTableEntryEncoding.SignedRelative32, dispatch.EntryEncoding);
        Assert.Null(dispatch.MappingTableAddress);
        Assert.Equal(function.DataAddress, dispatch.TableAddress);
        Assert.Equal(function.DataAddress, dispatch.EntryBaseAddress);
        Assert.Equal(case0Address, dispatch.GetCase(0).StartAddress);
        Assert.Equal(case1Address, dispatch.GetCase(1).StartAddress);
        Assert.Equal(case2Address, dispatch.GetCase(2).StartAddress);
    }

    [Fact]
    public void TryDecode_RejectsAnOrdinaryIndirectJumpWithoutATableShape()
    {
        using ExecutableFunction function = ExecutableFunction.Create(assembler =>
        {
            assembler.xor(eax, eax);
            assembler.jmp(rax);
        });
        using ScanRegion region = ScanRegion.FromPointer(function.EntryPoint, function.CodeLength);

        Assert.False(SwitchDispatchDecoder.TryDecode(
            region,
            function.Address,
            out SwitchDispatch? dispatch,
            out string? error));
        Assert.Null(dispatch);
        Assert.NotNull(error);
    }

    private static UInt64 FindAddress(ExecutableFunction function, params byte[] pattern)
    {
        byte[] bytes = function.OriginalBytes;
        for (int offset = 0; offset <= bytes.Length - pattern.Length; offset++)
        {
            int index = 0;
            while (index < pattern.Length && bytes[offset + index] == pattern[index])
                index++;

            if (index == pattern.Length)
                return function.Address + (UInt64)offset;
        }

        throw new InvalidOperationException("The generated case pattern was not found.");
    }

    private static byte[] BuildRelative32Table(UInt64 baseAddress, params UInt64[] targetAddresses)
    {
        byte[] table = new byte[checked(targetAddresses.Length * sizeof(Int32))];
        for (int index = 0; index < targetAddresses.Length; index++)
        {
            long relative = checked((long)targetAddresses[index] - (long)baseAddress);
            UInt32 encoded = unchecked((UInt32)checked((Int32)relative));
            int offset = index * sizeof(Int32);
            table[offset + 0] = (byte)encoded;
            table[offset + 1] = (byte)(encoded >> 8);
            table[offset + 2] = (byte)(encoded >> 16);
            table[offset + 3] = (byte)(encoded >> 24);
        }

        return table;
    }
}
