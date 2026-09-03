using System;
using System.Runtime.InteropServices;
using Xunit;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX86;
using RedBird.Core.Memory;
using RedBird.X86.Tests.Infrastructure;

namespace RedBird.X86.Tests;

/// <summary>Executes real 32-bit code under the x86 test host.</summary>
public sealed class X86DetourTests
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BinaryInt(int left, int right);

    [Fact]
    public void DirectDetour_RedirectsAndCallsTheRelocatedOriginal()
    {
        Assert.Equal(Architecture.X86, RuntimeInformation.ProcessArchitecture);

        using ExecutableX86Function target = X86TestFunctions.Add();
        BinaryInt call = target.GetDelegate<BinaryInt>();
        Assert.Equal(7, call(3, 4));

        X86Detour<BinaryInt>? detour = null;
        detour = (X86Detour<BinaryInt>)X86DetourBackend.Instance.CreateDetour(new DetourRequest<BinaryInt>
        {
            Name = "x86-add",
            TargetAddress = target.Address,
            Callback = (left, right) => detour!.Original(left, right) * 10,
        });

        using (detour)
        {
            Assert.Equal(HookState.Prepared, detour.State);
            Assert.Equal(X86DetourScheme.Direct, detour.Scheme);
            Assert.True(detour.DisplacedByteCount >= X86JumpFactory.PatchSize);
            Assert.Equal(7, detour.Original(3, 4));

            detour.Enable();
            Assert.Equal(70, call(3, 4));

            detour.Disable();
            Assert.Equal(7, call(3, 4));
        }
    }

    [Fact]
    public void IntermediaryFactory_WrapsInboundAndOriginalPaths()
    {
        using ExecutableX86Function target = X86TestFunctions.Add();
        BinaryInt call = target.GetDelegate<BinaryInt>();
        X86JumpIntermediaryFactory factory = new();

        X86Detour<BinaryInt>? detour = null;
        detour = (X86Detour<BinaryInt>)X86DetourBackend.Instance.CreateDetour(new DetourRequest<BinaryInt>
        {
            Name = "x86-intermediary",
            TargetAddress = target.Address,
            Callback = (left, right) => detour!.Original(left, right) + 100,
            IntermediaryFactory = factory,
        });

        Assert.Equal(DetourArchitecture.X86, factory.Context.Architecture);
        Assert.NotEqual(factory.Context.CallbackAddress, detour.HookEntryPointAddress);
        Assert.NotEqual(detour.TrampolineAddress, detour.OriginalEntryPointAddress);

        detour.Enable();
        Assert.Equal(107, call(3, 4));
        detour.Dispose();

        Assert.Equal(7, call(3, 4));
        Assert.True(factory.Lifetime.IsDisposed);
    }

    private sealed class X86JumpIntermediaryFactory : IDetourIntermediaryFactory
    {
        public DetourIntermediaryContext Context { get; private set; }
        public LifetimeProbe Lifetime { get; } = new();

        public DetourIntermediaryStubs Create(in DetourIntermediaryContext context)
        {
            Context = context;
            IntPtr inbound = BuildJump(context.CallbackAddress);
            IntPtr outbound = BuildJump(context.TrampolineAddress);
            return new DetourIntermediaryStubs(inbound, outbound, Lifetime);
        }

        private static IntPtr BuildJump(IntPtr destination)
        {
            IntPtr stub = NativeMemoryManager.AllocateStub(X86JumpFactory.PatchSize);
            if (stub == IntPtr.Zero)
                throw new InvalidOperationException("Could not allocate an x86 intermediary stub.");

            UInt64 sourceAddress = unchecked((UInt32)stub.ToInt32());
            UInt64 destinationAddress = unchecked((UInt32)destination.ToInt32());
            byte[] jump = X86JumpFactory.BuildDirect(sourceAddress, destinationAddress);
            if (!NativeMemoryManager.WriteStub(stub, jump))
                throw new InvalidOperationException("Could not write an x86 intermediary stub.");

            return stub;
        }
    }

    private sealed class LifetimeProbe : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }
}
