using System;
using Iced.Intel;
using Xunit;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.Backends.NativeX86;
using RedBird.Backends.PolyHook2;
using RedBird.Core.Memory;
using RedBird.X64.Tests.Infrastructure;

namespace RedBird.X64.Tests;

/// <summary>Contract and encoding tests for the calling-convention intermediary seam.</summary>
public sealed class DetourIntermediaryTests
{
    [Fact]
    public void NativeX64_RoutesBothDirectionsThroughDistinctIntermediaryStubs()
    {
        using ExecutableFunction target = TestFunctions.Add();
        X64JumpIntermediaryFactory factory = new();
        IDetour<TestDelegates.BinaryInt>? detour = null;

        detour = NativeDetourBackend.Instance.CreateDetour(new DetourRequest<TestDelegates.BinaryInt>
        {
            Name = "intermediary",
            TargetAddress = target.Address,
            Callback = (a, b) => detour!.Original(a, b) * 10,
            IntermediaryFactory = factory,
        });

        NativeDetour<TestDelegates.BinaryInt> native = Assert.IsType<NativeDetour<TestDelegates.BinaryInt>>(detour);
        Assert.Equal(DetourArchitecture.X64, factory.Context.Architecture);
        Assert.Equal(target.Address, factory.Context.TargetAddress);
        Assert.NotEqual(factory.Context.CallbackAddress, native.HookEntryPointAddress);
        Assert.NotEqual(native.TrampolineAddress, native.OriginalEntryPointAddress);

        TestDelegates.BinaryInt call = target.GetDelegate<TestDelegates.BinaryInt>();
        detour.Enable();
        Assert.Equal(70, call(3, 4));

        detour.Dispose();
        Assert.True(factory.Lifetime.IsDisposed);
    }

    [Fact]
    public void PolyHook2_RejectsIntermediaryFactoryInsteadOfIgnoringIt()
    {
        using ExecutableFunction target = TestFunctions.Add();

        Assert.Throws<NotSupportedException>(() =>
            PolyHook2Backend.Instance.CreateDetour(new DetourRequest<TestDelegates.BinaryInt>
            {
                Name = "unsupported-intermediary",
                TargetAddress = target.Address,
                Callback = (a, b) => a + b,
                IntermediaryFactory = new NeverCalledFactory(),
            }));
    }

    [Fact]
    public void X86DirectJump_WrapsAcrossThe32BitAddressSpace()
    {
        const UInt64 Source = 0xF000_0000;
        const UInt64 Destination = 0x1000_0000;

        byte[] code = X86JumpFactory.BuildDirect(Source, Destination);
        Decoder decoder = Decoder.Create(32, new ByteArrayCodeReader(code), Source);
        Instruction jump = decoder.Decode();

        Assert.Equal(5, code.Length);
        Assert.Equal(FlowControl.UnconditionalBranch, jump.FlowControl);
        Assert.Equal(Destination, jump.NearBranchTarget & UInt32.MaxValue);
    }

    [Fact]
    public void X86Backend_RejectsANonX86Process()
    {
        Assert.Throws<PlatformNotSupportedException>(() => new X86DetourBackend());
    }

    private sealed class X64JumpIntermediaryFactory : IDetourIntermediaryFactory
    {
        public DetourIntermediaryContext Context { get; private set; }
        public LifetimeProbe Lifetime { get; } = new();

        public DetourIntermediaryStubs Create(in DetourIntermediaryContext context)
        {
            Context = context;
            IntPtr hookEntry = BuildJump(context.TargetAddress, (UInt64)context.CallbackAddress);
            IntPtr originalEntry = BuildJump(context.TargetAddress, (UInt64)context.TrampolineAddress);
            return new DetourIntermediaryStubs(hookEntry, originalEntry, Lifetime);
        }

        private static IntPtr BuildJump(UInt64 anchor, UInt64 destination)
        {
            byte[] code = CodePatch.BuildAbsoluteJump(destination);
            IntPtr stub = NativeMemoryManager.AllocateStub(anchor, code.Length);
            if (stub == IntPtr.Zero || !NativeMemoryManager.WriteStub(stub, code))
                throw new InvalidOperationException("Could not build an intermediary test stub.");

            return stub;
        }
    }

    private sealed class LifetimeProbe : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    private sealed class NeverCalledFactory : IDetourIntermediaryFactory
    {
        public DetourIntermediaryStubs Create(in DetourIntermediaryContext context)
            => throw new InvalidOperationException("The unsupported backend called the factory.");
    }
}
