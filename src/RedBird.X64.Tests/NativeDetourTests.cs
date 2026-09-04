using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.X64.Tests.Infrastructure;
using System;
using System.Runtime.InteropServices;
using static Iced.Intel.AssemblerRegisters;
using Xunit;

namespace RedBird.X64.Tests;

/// <summary>
/// Tests for behaviour only the managed engine exposes: scheme selection, thunk resolution and the diagnostics it publishes.
/// </summary>
public sealed unsafe class NativeDetourTests
{
    private static NativeDetourBackend Backend(NativeDetourOptions options) => new(options);

    private static NativeDetour<T> Detour<T>(NativeDetourBackend backend, UInt64 address, T callback, string name = "test")
        where T : Delegate
        => (NativeDetour<T>)backend.CreateDetour(BackendCatalog.Request(address, callback, name));

    // -------------------------------------------------------------------------
    // Scheme selection
    // -------------------------------------------------------------------------

    [Fact]
    public void IndirectScheme_UsesSixBytesAndAPointerSlot()
    {
        NativeDetourBackend backend = Backend(new NativeDetourOptions { AllowedSchemes = DetourScheme.Indirect });

        using ExecutableFunction target = TestFunctions.Add();
        TestDelegates.BinaryInt callback = (a, b) => 0;

        using NativeDetour<TestDelegates.BinaryInt> detour = Detour(backend, target.Address, callback);

        Assert.Equal(DetourScheme.Indirect, detour.Scheme);
        Assert.NotEqual(IntPtr.Zero, detour.PointerSlot);

        // Six bytes of jump, rounded up to whatever instruction boundary follows.
        Assert.InRange(detour.DisplacedByteCount, 6, 16);

        // The slot has to hold the callback, or the jump goes somewhere interesting.
        UInt64 expected = (UInt64)Marshal.GetFunctionPointerForDelegate(callback);
        Assert.Equal(expected, *(UInt64*)detour.PointerSlot);
    }

    [Fact]
    public void AbsoluteScheme_UsesFourteenBytesAndNoSlot()
    {
        NativeDetourBackend backend = Backend(new NativeDetourOptions { AllowedSchemes = DetourScheme.Absolute });

        using ExecutableFunction target = TestFunctions.Add();

        using NativeDetour<TestDelegates.BinaryInt> detour = Detour<TestDelegates.BinaryInt>(backend, target.Address, (a, b) => 0);

        Assert.Equal(DetourScheme.Absolute, detour.Scheme);
        Assert.Equal(IntPtr.Zero, detour.PointerSlot);
        Assert.InRange(detour.DisplacedByteCount, 14, 24);
    }

    [Fact]
    public void AbsoluteScheme_IsRefusedOnAPrologueTooShortForIt()
    {
        NativeDetourBackend backend = Backend(new NativeDetourOptions { AllowedSchemes = DetourScheme.Absolute });

        // Nine bytes before the ret: enough for the indirect jump, not for the absolute one.
        using ExecutableFunction target = TestFunctions.SelfBranchingLoop();

        Assert.Throws<NotSupportedException>(() =>
            Detour<TestDelegates.NullaryInt>(backend, target.Address, () => 0));

        Assert.True(target.MatchesOriginal(target.CodeLength));
    }

    [Fact]
    public void SelfBranchingPrologue_IsWidenedToCoverTheBranch()
    {
        NativeDetourBackend backend = Backend(new NativeDetourOptions { AllowedSchemes = DetourScheme.Indirect });

        using ExecutableFunction target = TestFunctions.SelfBranchingLoop();

        using NativeDetour<TestDelegates.NullaryInt> detour = Detour<TestDelegates.NullaryInt>(backend, target.Address, () => 0);

        // Six bytes would stop mid-loop at offset 7; the jl at offset 7..8 targets offset 2, which is inside that range, so the displaced region has to reach at least offset 9.
        Assert.True(
            detour.DisplacedByteCount >= 9,
            $"Expected the prologue to widen to at least 9 bytes for the backwards branch, got {detour.DisplacedByteCount}.");
    }

    // -------------------------------------------------------------------------
    // Trampoline placement
    // -------------------------------------------------------------------------

    [Fact]
    public void Trampoline_IsAllocatedWithinRelativeBranchRange()
    {
        NativeDetourBackend backend = Backend(NativeDetourOptions.Default);

        using ExecutableFunction target = TestFunctions.Add();

        using NativeDetour<TestDelegates.BinaryInt> detour = Detour<TestDelegates.BinaryInt>(backend, target.Address, (a, b) => 0);

        UInt64 trampoline = (UInt64)detour.TrampolineAddress;
        UInt64 distance = trampoline > target.Address ? trampoline - target.Address : target.Address - trampoline;

        // The whole relocation design rests on this: inside 2 GB, every displacement can be re-encoded, and Iced never has to fall back to the "too far away" error.
        Assert.True(distance < 0x8000_0000UL, $"Trampoline is {distance:X} bytes from the target, outside rel32 range.");
        Assert.True(detour.TrampolineSize > 0);
    }

    [Fact]
    public void Original_IsCallableBeforeEnable()
    {
        NativeDetourBackend backend = Backend(NativeDetourOptions.Default);

        using ExecutableFunction target = TestFunctions.Add();

        using NativeDetour<TestDelegates.BinaryInt> detour = Detour<TestDelegates.BinaryInt>(backend, target.Address, (a, b) => 0);

        // The trampoline is built during preparation, so it works before the target is patched.
        // PolyHook cannot do this, it publishes the trampoline partway through its hook() which is why this test lives here rather than in the shared contract suite.
        Assert.Equal(HookState.Prepared, detour.State);
        Assert.Equal(7, detour.Original(3, 4));
    }

    [Fact]
    public void CommonWindowsCompilerPrologue_CanBePrepared()
    {
        // Exact prefix from the reported real-world failure:
        // mov [rsp+8],rbx; push rdi; movsxd rbx,edx; mov rdi,rcx; mov eax,ebx
        // It provides six bytes for Indirect and fourteen for Absolute.
        using ExecutableFunction target = ExecutableFunction.Create(static assembler =>
        {
            assembler.db(new byte[]
            {
                0x48, 0x89, 0x5C, 0x24, 0x08,
                0x57,
                0x48, 0x63, 0xDA,
                0x48, 0x8B, 0xF9,
                0x8B, 0xC3,
                0x5F,
                0xC3,
            });
        });

        NativeDetourBackend backend = Backend(NativeDetourOptions.Default);
        using NativeDetour<TestDelegates.BinaryInt> detour =
            Detour<TestDelegates.BinaryInt>(backend, target.Address, (a, b) => 0, "compiler-prologue");

        Assert.Equal(DetourScheme.Indirect, detour.Scheme);
        Assert.InRange(detour.DisplacedByteCount, 6, 14);
        Assert.NotEqual(IntPtr.Zero, detour.TrampolineAddress);
    }

    [Fact]
    public void ForwardCall_DoesNotMergeTheCalleeIntoPrologueAnalysis()
    {
        using ExecutableFunction target = ExecutableFunction.Create(static assembler =>
        {
            Label entry = assembler.CreateLabel();
            Label nearbyCallee = assembler.CreateLabel();

            assembler.Label(ref entry);
            assembler.db(new byte[]
            {
                0x48, 0x89, 0x5C, 0x24, 0x08,
                0x57,
            });
            assembler.call(nearbyCallee);
            assembler.pop(rdi);
            assembler.ret();

            for (int i = 0; i < 16; i++)
                assembler.nop();

            assembler.Label(ref nearbyCallee);
            assembler.call(entry);
            assembler.ret();
        });

        NativeDetourBackend backend = Backend(new NativeDetourOptions
        {
            AllowedSchemes = DetourScheme.Indirect,
        });

        using NativeDetour<TestDelegates.NullaryInt> detour =
            Detour<TestDelegates.NullaryInt>(backend, target.Address, () => 0, "forward-call");

        // Only the six-byte compiler prefix is needed. Before the fix, the forward call caused
        // the analyzer to scan past the caller's ret and a call in the callee widened this to an
        // impossible cross-function prologue.
        Assert.Equal(6, detour.DisplacedByteCount);
    }

    [Fact]
    public void RecursiveCallToEntry_DoesNotWidenTheDisplacedPrologue()
    {
        using ExecutableFunction target = ExecutableFunction.Create(static assembler =>
        {
            Label entry = assembler.CreateLabel();
            assembler.Label(ref entry);
            assembler.db(new byte[]
            {
                0x48, 0x89, 0x5C, 0x24, 0x08,
                0x57,
            });
            assembler.call(entry);
            assembler.pop(rdi);
            assembler.ret();
        });

        NativeDetourBackend backend = Backend(new NativeDetourOptions
        {
            AllowedSchemes = DetourScheme.Indirect,
        });

        using NativeDetour<TestDelegates.NullaryInt> detour =
            Detour<TestDelegates.NullaryInt>(backend, target.Address, () => 0, "recursive-call");

        Assert.Equal(6, detour.DisplacedByteCount);
    }

    // -------------------------------------------------------------------------
    // Thunks
    // -------------------------------------------------------------------------

    [Fact]
    public void FollowJumps_ResolvesToTheFunctionBody()
    {
        NativeDetourBackend backend = Backend(new NativeDetourOptions { FollowJumps = true });

        using ExecutableFunction target = TestFunctions.ThunkTo(0x42, out UInt64 bodyOffset);

        using NativeDetour<TestDelegates.NullaryInt> detour = Detour<TestDelegates.NullaryInt>(backend, target.Address, () => 0x99);

        Assert.Equal(target.Address + bodyOffset, detour.TargetAddress);
    }

    [Fact]
    public void FollowJumpsDisabled_HooksTheThunkItself()
    {
        NativeDetourBackend backend = Backend(new NativeDetourOptions { FollowJumps = false });

        using ExecutableFunction target = TestFunctions.ThunkTo(0x42, out _);

        using NativeDetour<TestDelegates.NullaryInt> detour = Detour<TestDelegates.NullaryInt>(backend, target.Address, () => 0x99);

        Assert.Equal(target.Address, detour.TargetAddress);
    }

    [Fact]
    public void RefusePolicy_RejectsAPrologueThatAlreadyJumpsAway()
    {
        NativeDetourBackend backend = Backend(new NativeDetourOptions
        {
            FollowJumps = false,
            Chaining = HookChainingPolicy.Refuse,
        });

        using ExecutableFunction target = TestFunctions.ThunkTo(0x42, out _);

        // With following off, the thunk's jmp is indistinguishable from an installed detour, and
        // Refuse means exactly that: do not layer.
        Assert.Throws<NotSupportedException>(() =>
            Detour<TestDelegates.NullaryInt>(backend, target.Address, () => 0x99));
    }

    // -------------------------------------------------------------------------
    // Backend surface
    // -------------------------------------------------------------------------

    [Fact]
    public void Backend_RejectsNullAndZeroRequests()
    {
        NativeDetourBackend backend = Backend(NativeDetourOptions.Default);

        Assert.Throws<ArgumentException>(() =>
            backend.CreateDetour(BackendCatalog.Request<TestDelegates.NullaryInt>(0, () => 0)));

        using ExecutableFunction target = TestFunctions.Add();

        Assert.Throws<ArgumentException>(() =>
            backend.CreateDetour(new DetourRequest<TestDelegates.NullaryInt>
            {
                Name = "null-callback",
                TargetAddress = target.Address,
                Callback = null!,
            }));
    }

    [Fact]
    public void PreparationFailure_ReportsPerSchemeAttempts()
    {
        using ExecutableFunction target = TestFunctions.Add();
        NativeDetourBackend backend = Backend(new NativeDetourOptions
        {
            AllowedSchemes = DetourScheme.None,
        });

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            Detour<TestDelegates.BinaryInt>(backend, target.Address, (a, b) => 0, "no-schemes"));

        Assert.Contains("No schemes were enabled", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Backend_ReportsItsName()
    {
        Assert.Equal("NativeX64", NativeDetourBackend.Instance.Name);
    }
}
