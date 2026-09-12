using System;
using Iced.Intel;
using RedBird.Core.Memory;
using RedBird.X86.Assembly;
using RedBird.X86.Assembly.InstructionWalker;
using RedBird.X86.Assembly.Switches;
using Xunit;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.X86.Tests;

public sealed class SwitchDispatchTests
{
    [Fact]
    public void Decode_ResolvesAByteMappedAbsolutePointerTableAndBoundaries()
    {
        const int mappingOffset = 0x800;
        const int jumpTableOffset = mappingOffset + 0x10;

        using ExecutableX86Function function = ExecutableX86Function.Create((assembler, layout) =>
        {
            Label case0 = assembler.CreateLabel();
            Label case1And2 = assembler.CreateLabel();
            Label case3 = assembler.CreateLabel();
            Label defaultCase = assembler.CreateLabel();

            assembler.sub(eax, 0x65);
            assembler.cmp(eax, 3);
            assembler.ja(defaultCase);

            // movzx eax, byte ptr [eax + mappingAddress]
            assembler.db(BuildMovzxEaxFromEax((UInt32)(layout.CodeAddress + mappingOffset)));
            // jmp dword ptr [eax*4 + jumpTableAddress]
            assembler.db(BuildAbsoluteIndexedJump((UInt32)(layout.CodeAddress + jumpTableOffset)));

            assembler.Label(ref case0);
            assembler.mov(eax, 0x10);
            assembler.nop();

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
            BuildUInt32Table(case0Address, case1Address, case3Address));

        using ScanRegion region = ScanRegion.FromPointer(function.EntryPoint, 4096, "mapped-x86-switch");
        Assert.True(
            SwitchDispatchDecoder.TryDecode(region, function.Address, out SwitchDispatch? dispatch, out string? error),
            error);
        Assert.NotNull(dispatch);

        Assert.Equal(SwitchTableLayout.ByteMapped, dispatch.Layout);
        Assert.Equal(SwitchTableEntryEncoding.AbsolutePointer32, dispatch.EntryEncoding);
        Assert.Equal((UInt64?)(function.Address + mappingOffset), dispatch.MappingTableAddress);
        Assert.Equal(function.Address + jumpTableOffset, dispatch.TableAddress);
        Assert.Equal(0UL, dispatch.EntryBaseAddress);
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
        Assert.Equal(defaultAddress + 6, dispatch.FunctionEndAddressExclusive);

        InstructionWalker walker = case0.CreateWalker(new InstructionWalkerConfig
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

        Assert.True(walker.TryWalk(out UInt32 immediate));
        Assert.Equal(0x10u, immediate);
        Assert.False(walker.TryWalk(out UInt32 _));
        Assert.Equal(case0.EndAddressExclusive, walker.Instructions[^1].NextIP);
    }

    [Fact]
    public void Decode_ResolvesAStaticallyBasedRelativeTableAndRegisterAlias()
    {
        using ExecutableX86Function function = ExecutableX86Function.Create((assembler, layout) =>
        {
            Label case0 = assembler.CreateLabel();
            Label case1 = assembler.CreateLabel();
            Label case2 = assembler.CreateLabel();
            Label defaultCase = assembler.CreateLabel();

            assembler.cmp(edi, 2);
            assembler.ja(defaultCase);
            assembler.mov(eax, edi);
            assembler.mov(edx, (UInt32)layout.DataAddress);
            // mov ecx, dword ptr [edx + eax*4]
            assembler.db(new byte[] { 0x8B, 0x0C, 0x82 });
            assembler.add(ecx, edx);
            assembler.jmp(ecx);

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

        using ScanRegion region = ScanRegion.FromPointer(function.EntryPoint, 4096, "relative-x86-switch");
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
    public void Decode_ResolvesAnAbsolutePointerTableLoadedIntoARegister()
    {
        const int jumpTableOffset = 0x800;

        using ExecutableX86Function function = ExecutableX86Function.Create((assembler, layout) =>
        {
            Label case0 = assembler.CreateLabel();
            Label case1 = assembler.CreateLabel();
            Label defaultCase = assembler.CreateLabel();

            assembler.cmp(ecx, 1);
            assembler.ja(defaultCase);
            // mov eax, dword ptr [ecx*4 + jumpTableAddress]
            assembler.db(BuildAbsoluteIndexedLoad((UInt32)(layout.CodeAddress + jumpTableOffset)));
            assembler.jmp(eax);

            assembler.Label(ref case0);
            assembler.mov(eax, 0x30);
            assembler.ret();

            assembler.Label(ref case1);
            assembler.mov(eax, 0x31);
            assembler.ret();

            assembler.Label(ref defaultCase);
            assembler.mov(eax, 0x3F);
            assembler.ret();
        });

        UInt64 case0Address = FindAddress(function, 0xB8, 0x30, 0x00, 0x00, 0x00, 0xC3);
        UInt64 case1Address = FindAddress(function, 0xB8, 0x31, 0x00, 0x00, 0x00, 0xC3);
        CodePatch.Write(
            function.Address + jumpTableOffset,
            BuildUInt32Table(case0Address, case1Address));

        using ScanRegion region = ScanRegion.FromPointer(function.EntryPoint, 4096, "register-absolute-x86-switch");
        SwitchDispatch dispatch = SwitchDispatchDecoder.Decode(region, function.Address);

        Assert.Equal(SwitchTableLayout.Direct, dispatch.Layout);
        Assert.Equal(SwitchTableEntryEncoding.AbsolutePointer32, dispatch.EntryEncoding);
        Assert.Equal(function.Address + jumpTableOffset, dispatch.TableAddress);
        Assert.Equal(0UL, dispatch.EntryBaseAddress);
        Assert.Equal(case0Address, dispatch.GetCase(0).StartAddress);
        Assert.Equal(case1Address, dispatch.GetCase(1).StartAddress);
    }

    [Fact]
    public void TryDecode_RejectsAnOrdinaryIndirectJumpWithoutATableShape()
    {
        using ExecutableX86Function function = ExecutableX86Function.Create(assembler =>
        {
            assembler.xor(eax, eax);
            assembler.jmp(eax);
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

    private static UInt64 FindAddress(ExecutableX86Function function, params byte[] pattern)
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

        throw new InvalidOperationException("The generated x86 case pattern was not found.");
    }

    private static byte[] BuildMovzxEaxFromEax(UInt32 mappingAddress)
        => [0x0F, 0xB6, 0x80, .. GetBytes(mappingAddress)];

    private static byte[] BuildAbsoluteIndexedJump(UInt32 tableAddress)
        => [0xFF, 0x24, 0x85, .. GetBytes(tableAddress)];

    private static byte[] BuildAbsoluteIndexedLoad(UInt32 tableAddress)
        => [0x8B, 0x04, 0x8D, .. GetBytes(tableAddress)];

    private static byte[] BuildUInt32Table(params UInt64[] values)
    {
        byte[] table = new byte[checked(values.Length * sizeof(UInt32))];
        for (int index = 0; index < values.Length; index++)
            WriteUInt32(table, index * sizeof(UInt32), checked((UInt32)values[index]));

        return table;
    }

    private static byte[] BuildRelative32Table(UInt64 baseAddress, params UInt64[] targetAddresses)
    {
        byte[] table = new byte[checked(targetAddresses.Length * sizeof(Int32))];
        for (int index = 0; index < targetAddresses.Length; index++)
        {
            long relative = checked((long)targetAddresses[index] - (long)baseAddress);
            WriteUInt32(table, index * sizeof(Int32), unchecked((UInt32)checked((Int32)relative)));
        }

        return table;
    }

    private static byte[] GetBytes(UInt32 value)
    {
        byte[] bytes = new byte[sizeof(UInt32)];
        WriteUInt32(bytes, 0, value);
        return bytes;
    }

    private static void WriteUInt32(byte[] destination, int offset, UInt32 value)
    {
        destination[offset + 0] = (byte)value;
        destination[offset + 1] = (byte)(value >> 8);
        destination[offset + 2] = (byte)(value >> 16);
        destination[offset + 3] = (byte)(value >> 24);
    }
}
