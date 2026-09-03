using System;
using System.Collections.Generic;
using System.Linq;
using Iced.Intel;
using Xunit;
using RedBird.Core.Memory;
using RedBird.X64.Tests.Infrastructure;
using RedBird.X64.Assembly.InstructionWalker;
using RedBird.X64.Assembly.Stateful;
using RedBird.X64.Memory.Scanners;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.X64.Tests;

/// <summary>
/// Tests for the fluent scanner and the instruction walker, the pair used to locate a function that has no stable pattern of its own, and then find an anchor instruction inside it.
/// </summary>
/// <remarks>
/// The shape under test is the one real consumers rely on:
/// <code>
/// scanner.Scan("E8 ?? ?? ?? ?? 49 0F BF 8C 3E").TryReadFlowControlTarget(out UInt64 target);
/// InstructionWalker walker = new(target, config);
/// walker.TryWalk(out UInt64 anchor);
/// </code>
/// Both halves work on real decoded instructions, so the fixtures assemble real code rather than hand-writing bytes.
/// </remarks>
public sealed class ScannerAndWalkerTests
{
    // -------------------------------------------------------------------------
    // DataScanner
    // -------------------------------------------------------------------------

    [Fact]
    public void Scan_LocatesAPatternAndReportsItsAddress()
    {
        using ExecutableFunction target = TestFunctions.Constant(0x1234);
        using ScanRegion region = ScanRegion.FromPointer((IntPtr)target.Address, target.CodeLength);

        DataScanner scanner = DataScanner.Create(region);

        // mov eax, imm32 is B8 <imm32>.
        scanner.Scan("B8 34 12 00 00");

        Assert.True(scanner.Found);
        Assert.Equal(target.Address, scanner.CurrentAddress);
    }

    [Fact]
    public void Scan_ReportsFailureWithoutThrowing()
    {
        using ExecutableFunction target = TestFunctions.Constant(1);
        using ScanRegion region = ScanRegion.FromPointer((IntPtr)target.Address, target.CodeLength);

        DataScanner scanner = DataScanner.Create(region).Scan("DE AD BE EF CA FE");

        Assert.False(scanner.Found);

        // A failed scan must not produce a usable-looking address downstream.
        Assert.False(scanner.TryReadFlowControlTarget(out _));
        Assert.False(scanner.TryGetManagedImmediate(out ManagedAssemblyImmediate<UInt16>? site));
        Assert.Null(site);
    }

    [Fact]
    public void Scan_AppliesTheOffset()
    {
        using ExecutableFunction target = TestFunctions.Constant(0x1234);
        using ScanRegion region = ScanRegion.FromPointer((IntPtr)target.Address, target.CodeLength);

        DataScanner scanner = DataScanner.Create(region).Scan("B8 34 12 00 00", offset: 5);

        Assert.Equal(target.Address + 5, scanner.CurrentAddress);
    }

    [Fact]
    public void TryReadFlowControlTarget_ResolvesACallTarget()
    {
        // The exact manoeuvre from the consumer paste: a function with no stable anchor of its own, reached by reading the target of a call that does have one.
        using ExecutableFunction callee = TestFunctions.Constant(0x5150);

        UInt64 calleeAddress = callee.Address;

        using ExecutableFunction caller = ExecutableFunction.Create((asm, _) =>
        {
            asm.push(rbx);
            asm.AddInstruction(Instruction.CreateBranch(Code.Call_rel32_64, calleeAddress));
            asm.pop(rbx);
            asm.ret();
        });

        using ScanRegion region = ScanRegion.FromPointer((IntPtr)caller.Address, caller.CodeLength);

        // 53 = push rbx, E8 = call rel32.
        DataScanner scanner = DataScanner.Create(region).Scan("53 E8", offset: 1);

        Assert.True(scanner.TryReadFlowControlTarget(out UInt64 resolved));
        Assert.Equal(calleeAddress, resolved);
    }

    [Fact]
    public void ScanAll_FindsEveryOccurrence()
    {
        using ExecutableFunction target = ExecutableFunction.Create((asm, _) =>
        {
            asm.mov(eax, 0x41);
            asm.nop();
            asm.mov(eax, 0x41);
            asm.nop();
            asm.mov(eax, 0x41);
            asm.ret();
        });

        using ScanRegion region = ScanRegion.FromPointer((IntPtr)target.Address, target.CodeLength);

        IReadOnlyList<UInt64> matches = DataScanner.Create(region).ScanAll("B8 41 00 00 00");

        Assert.Equal(3, matches.Count);
        Assert.Equal(target.Address, matches[0]);
        Assert.All(matches, address => Assert.InRange(address, target.Address, target.Address + (UInt64)target.CodeLength));
    }

    [Fact]
    public void At_BypassesTheScan()
    {
        using ExecutableFunction target = TestFunctions.Constant(7);
        using ScanRegion region = ScanRegion.FromPointer((IntPtr)target.Address, target.CodeLength);

        DataScanner scanner = DataScanner.Create(region).At(target.Address).Advance(2);

        Assert.True(scanner.Found);
        Assert.Equal(target.Address + 2, scanner.CurrentAddress);
    }

    // -------------------------------------------------------------------------
    // InstructionWalker
    // -------------------------------------------------------------------------

    /// <summary>A function with a recognisable instruction mix to walk over.</summary>
    private static ExecutableFunction WalkableFunction()
        => ExecutableFunction.Create((asm, _) =>
        {
            asm.push(rbx);                                   // 53
            asm.mov(eax, 0x11);                              // B8 11000000
            asm.movzx(ecx, __word_ptr[rbx + 0x20]);          // <- anchor
            asm.add(eax, ecx);
            asm.mov(edx, 0x22);
            asm.pop(rbx);
            asm.ret();
        });

    private static InstructionWalker Walker(ExecutableFunction function, params InstructionWalkRule[] rules)
        => new(function.Address, new InstructionWalkerConfig { WalkRules = rules });

    [Fact]
    public void TryWalk_FindsTheFirstMatchingInstruction()
    {
        using ExecutableFunction function = WalkableFunction();

        InstructionWalker walker = Walker(function, new InstructionWalkRule
        {
            Predicate = InstructionWalkerHelpers.IsMnemonic(Mnemonic.Movzx),
            Extractor = InstructionWalkerHelpers.ExtractInstructionAddress,
        });

        Assert.True(walker.TryWalk(out UInt64 anchor));

        // push rbx (1) + mov eax, imm32 (5) = 6.
        Assert.Equal(function.Address + 6, anchor);
    }

    [Fact]
    public void TryWalk_ExtractsAnImmediate()
    {
        using ExecutableFunction function = WalkableFunction();

        InstructionWalker walker = Walker(function, new InstructionWalkRule
        {
            Predicate = InstructionWalkerHelpers.IsMov,
            Extractor = InstructionWalkerHelpers.ExtractImmediate,
        });

        Assert.True(walker.TryWalk(out UInt32 value));
        Assert.Equal(0x11u, value);
    }

    [Fact]
    public void TryWalk_ExtractsADisplacement()
    {
        using ExecutableFunction function = WalkableFunction();

        InstructionWalker walker = Walker(function, new InstructionWalkRule
        {
            Predicate = InstructionWalkerHelpers.IsMnemonic(Mnemonic.Movzx),
            Extractor = InstructionWalkerHelpers.ExtractDisplacement,
        });

        Assert.True(walker.TryWalk(out UInt64 displacement));
        Assert.Equal(0x20u, displacement);
    }

    [Fact]
    public void TryWalk_HonoursInstructionSkip()
    {
        using ExecutableFunction function = WalkableFunction();

        InstructionWalker walker = Walker(function, new InstructionWalkRule
        {
            InstructionSkip = 2,                              // past push and the first mov
            Extractor = InstructionWalkerHelpers.ExtractInstructionAddress,
        });

        Assert.True(walker.TryWalk(out UInt64 address));
        Assert.Equal(function.Address + 6, address);
    }

    [Fact]
    public void TryWalk_ReturnsFalseWhenNothingMatches()
    {
        using ExecutableFunction function = WalkableFunction();

        InstructionWalker walker = Walker(function, new InstructionWalkRule
        {
            Predicate = InstructionWalkerHelpers.IsMnemonic(Mnemonic.Vaddps),
            Extractor = InstructionWalkerHelpers.ExtractInstructionAddress,
        });

        Assert.False(walker.TryWalk(out UInt64 _));
    }

    [Fact]
    public void TryWalk_EnumeratesSuccessiveMatches()
    {
        using ExecutableFunction function = WalkableFunction();

        InstructionWalker walker = Walker(function, new InstructionWalkRule
        {
            Predicate = InstructionWalkerHelpers.IsMov,
            Extractor = InstructionWalkerHelpers.ExtractImmediate,
        });

        // A single-rule walker acts as a cursor: each call returns the next match.
        Assert.True(walker.TryWalk(out UInt32 first));
        Assert.True(walker.TryWalk(out UInt32 second));
        Assert.False(walker.TryWalk(out UInt32 _));

        Assert.Equal(0x11u, first);
        Assert.Equal(0x22u, second);

        walker.Reset();
        Assert.True(walker.TryWalk(out UInt32 afterReset));
        Assert.Equal(0x11u, afterReset);
    }

    [Fact]
    public void Matches_IsIndependentOfTheCursor()
    {
        using ExecutableFunction function = WalkableFunction();

        InstructionWalkRule rule = new()
        {
            Predicate = InstructionWalkerHelpers.IsMov,
            Extractor = InstructionWalkerHelpers.ExtractImmediate,
        };

        InstructionWalker walker = Walker(function, rule);

        walker.TryWalk(out UInt32 _);   // advance the cursor

        // Matches and Extract are stateless views over the same disassembly, so they compose with
        // LINQ and are unaffected by whatever TryWalk has consumed.
        Assert.Equal(2, walker.Matches(rule).Count());
        Assert.Equal([0x11u, 0x22u], walker.Extract<UInt32>(rule).ToArray());
    }

    [Fact]
    public void MultiRule_FollowsACallIntoTheCallee()
    {
        using ExecutableFunction callee = ExecutableFunction.Create((asm, _) =>
        {
            asm.mov(eax, 0x7A7A);
            asm.ret();
        });

        UInt64 calleeAddress = callee.Address;

        using ExecutableFunction caller = ExecutableFunction.Create((asm, _) =>
        {
            asm.push(rbx);
            asm.AddInstruction(Instruction.CreateBranch(Code.Call_rel32_64, calleeAddress));
            asm.pop(rbx);
            asm.ret();
        });

        // Rule 1 has no extractor, so the walker follows the call and hands control to rule 2, which then reads the immediate out of the callee.
        InstructionWalker walker = new(caller.Address, new InstructionWalkerConfig
        {
            WalkRules =
            [
                new InstructionWalkRule { Predicate = InstructionWalkerHelpers.IsCall },
                new InstructionWalkRule
                {
                    Predicate = InstructionWalkerHelpers.IsMov,
                    Extractor = InstructionWalkerHelpers.ExtractImmediate,
                },
            ],
        });

        Assert.True(walker.TryWalk(out UInt32 value));
        Assert.Equal(0x7A7Au, value);
    }

    [Fact]
    public void Predicates_Compose()
    {
        using ExecutableFunction function = WalkableFunction();

        InstructionWalker walker = Walker(function, new InstructionWalkRule
        {
            Predicate = InstructionWalkerHelpers.And(
                InstructionWalkerHelpers.IsMov,
                static instr => instr.GetImmediate(1) == 0x22),
            Extractor = InstructionWalkerHelpers.ExtractInstructionAddress,
        });

        Assert.True(walker.TryWalk(out UInt64 address));
        Assert.True(address > function.Address);
    }
}
