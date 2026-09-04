using System;
using System.Runtime.InteropServices;
using Xunit;
using RedBird.Core.Memory;
using RedBird.Core.PInvoke;
using RedBird.Core.Platform;

namespace RedBird.X64.Tests;

/// <summary>
/// Tests for the near-allocator that hands out trampolines and pointer slots.
/// </summary>
/// <remarks>
/// These exist because the same mistake has now been made three times in this code base: treating a bump allocation inside a shared slab as if it were an allocation the caller owns. 
/// Freeing one unmapped the slab; revoking write access from one revoked it from every stub on the same page. 
/// A test at this level catches the next variant before it reaches a detour.
/// </remarks>
public sealed unsafe class StubAllocatorTests
{
    /// <summary>An address in this process to allocate near. Any code address will do.</summary>
    private static UInt64 SomeCodeAddress => (UInt64)Marshal.GetFunctionPointerForDelegate<Action>(static () => { });

    [Fact]
    public void SuccessiveStubs_AreAllWritable()
    {
        UInt64 anchor = SomeCodeAddress;

        // The crash this guards: the first stub was written and then had its page flipped to execute-read, so the second stub, which was handed out of the same page, faulted on its first store.
        // It only showed up on the second detour of a function, which made it look like a chaining bug rather than an allocator one.
        for (int i = 0; i < 16; i++)
        {
            IntPtr stub = NativeMemoryManager.AllocateStub(anchor, 64);
            Assert.NotEqual(IntPtr.Zero, stub);

            byte[] payload = new byte[64];
            for (int b = 0; b < payload.Length; b++)
                payload[b] = (byte)(i + b);

            Assert.True(NativeMemoryManager.WriteStub(stub, payload), $"Stub {i} was not writable.");

            for (int b = 0; b < payload.Length; b++)
                Assert.Equal(payload[b], ((byte*)stub)[b]);
        }
    }

    [Fact]
    public void StubsAreAllocatedWithinRelativeBranchRange()
    {
        UInt64 anchor = SomeCodeAddress;

        IntPtr stub = NativeMemoryManager.AllocateStub(anchor, 64);
        Assert.NotEqual(IntPtr.Zero, stub);

        UInt64 address = (UInt64)stub;
        UInt64 distance = address > anchor ? address - anchor : anchor - address;

        Assert.True(distance < 0x8000_0000UL, $"Stub is {distance:X} bytes from the anchor, outside rel32 range.");
    }

    [Fact]
    public void WriteStub_RoundTripsAPointerSizedValue()
    {
        IntPtr slot = NativeMemoryManager.AllocateStub(SomeCodeAddress, sizeof(UInt64));
        Assert.NotEqual(IntPtr.Zero, slot);

        const UInt64 Value = 0x0123_4567_89AB_CDEFUL;

        Assert.True(NativeMemoryManager.WriteStub(slot, Value));
        Assert.Equal(Value, *(UInt64*)slot);
    }

    [Fact]
    public void FinalizeStub_RefusesASlabAllocation()
    {
        IntPtr stub = NativeMemoryManager.AllocateStub(SomeCodeAddress, 64);
        Assert.NotEqual(IntPtr.Zero, stub);

        // Loud failure rather than a page-protection change that breaks the next caller.
        Assert.Throws<InvalidOperationException>(() => NativeMemoryManager.FinalizeStub(stub, 64));
    }

    [Fact]
    public void FinalizeStub_AcceptsAStandaloneAllocation()
    {
        IntPtr region = NativeMemoryManager.Alloc(4096, MemoryProtection.ReadWrite);
        Assert.NotEqual(IntPtr.Zero, region);

        try
        {
            // A region the caller owns outright is exactly what FinalizeStub is for.
            Assert.True(NativeMemoryManager.FinalizeStub(region, 4096));
        }
        finally
        {
            NativeMemoryManager.Free(region, 4096);
        }
    }

    [Fact]
    public void OlderSlab_RemainsTrackedAfterANewerSlabIsAllocated()
    {
        UInt64 anchor = SomeCodeAddress;
        IntPtr older = NativeMemoryManager.AllocateStub(anchor, 64);
        Assert.NotEqual(IntPtr.Zero, older);

        // A full-slab request cannot fit after the allocation above, so this necessarily rotates the allocator to another slab.
        IntPtr newer = NativeMemoryManager.AllocateStub(anchor, 64 * 1024);
        Assert.NotEqual(IntPtr.Zero, newer);
        Assert.NotEqual(older, newer);

        Assert.True(NativeMemoryManager.WriteStub(older, new byte[] { 0x90, 0xC3 }));
        Assert.Throws<InvalidOperationException>(() => NativeMemoryManager.FinalizeStub(older, 64));
    }

    [Fact]
    public void UnconstrainedStub_IsWritableAndExecutableManagedMemory()
    {
        IntPtr stub = NativeMemoryManager.AllocateStub(16);
        Assert.NotEqual(IntPtr.Zero, stub);
        Assert.True(NativeMemoryManager.WriteStub(stub, new byte[] { 0x90, 0xC3 }));
    }

    [Fact]
    public void WindowsFallbackAllocator_StaysInsideTheRequestedRange()
    {
        Assert.SkipUnless(PlatformInfo.IsWindows, "The VirtualQuery fallback is Windows-specific.");

        NativeMethods.Win32SystemInfo info = default;
        NativeMethods.GetNativeSystemInfo(&info);

        const UInt64 Reach = 0x7000_0000UL;
        const UInt64 Size = 64 * 1024;
        UInt64 anchor = SomeCodeAddress;
        UInt64 low = anchor > Reach ? anchor - Reach : 0;
        UInt64 high = anchor + Reach;

        UInt64 allocation = NativeMemoryManager.WindowsBoundAllocFallback(
            low,
            high,
            Size,
            MemoryProtection.ExecuteReadWrite,
            info.dwAllocationGranularity);

        Assert.NotEqual(0UL, allocation);
        try
        {
            Assert.InRange(allocation, low, high - Size);
        }
        finally
        {
            if (allocation != 0)
                NativeMemoryManager.Free((IntPtr)allocation, checked((int)Size));
        }
    }
}
