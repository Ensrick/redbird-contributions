using System;
using Xunit;
using RedBird.Core.Memory;
using RedBird.Core.PInvoke;
using RedBird.Core.Platform;

namespace RedBird.X86.Tests;

/// <summary>Regression tests for patches which cross a page-protection boundary.</summary>
public sealed unsafe class MemoryProtectionBoundaryTests
{
    [Fact]
    public void OSMemoryCodeWriter_UnprotectsOnceAndRestoresEveryTouchedPage()
    {
        int pageSize = PlatformInfo.PageSize;
        int allocationSize = checked(pageSize * 2);
        IntPtr allocation = NativeMemoryManager.Alloc(allocationSize, MemoryProtection.ReadWrite);
        Assert.NotEqual(IntPtr.Zero, allocation);

        try
        {
            IntPtr secondPage = allocation + pageSize;
            Assert.True(NativeMemoryManager.Protect(allocation, pageSize, MemoryProtection.Read, out _));
            Assert.True(NativeMemoryManager.Protect(secondPage, pageSize, MemoryProtection.ReadWrite, out _));

            using (OSMemoryCodeWriter writer = new((byte*)(secondPage - 1), 2))
            {
                writer.WriteByte(0x90);
                writer.WriteByte(0x90);
            }

            Assert.Equal(MemoryProtection.Read, NativeMemoryManager.QueryProtection((UInt64)allocation));
            Assert.Equal(MemoryProtection.ReadWrite, NativeMemoryManager.QueryProtection((UInt64)secondPage));
        }
        finally
        {
            NativeMemoryManager.Free(allocation, allocationSize);
        }
    }

    [Fact]
    public void CodePatch_RestoresEveryTouchedPageIndependently()
    {
        int pageSize = PlatformInfo.PageSize;
        int allocationSize = checked(pageSize * 2);
        IntPtr allocation = NativeMemoryManager.Alloc(allocationSize, MemoryProtection.ReadWrite);
        Assert.NotEqual(IntPtr.Zero, allocation);

        try
        {
            IntPtr secondPage = allocation + pageSize;
            Assert.True(NativeMemoryManager.Protect(allocation, pageSize, MemoryProtection.Read, out _));
            Assert.True(NativeMemoryManager.Protect(secondPage, pageSize, MemoryProtection.ReadWrite, out _));

            UInt64 patchAddress = (UInt64)(secondPage - 3);
            CodePatch.Write(patchAddress, new byte[5]);

            Assert.Equal(MemoryProtection.Read, NativeMemoryManager.QueryProtection((UInt64)allocation));
            Assert.Equal(MemoryProtection.ReadWrite, NativeMemoryManager.QueryProtection((UInt64)secondPage));
        }
        finally
        {
            NativeMemoryManager.Free(allocation, allocationSize);
        }
    }

    [Fact]
    public void Protect_RefusesAHeterogeneousRangeWithoutChangingIt()
    {
        int pageSize = PlatformInfo.PageSize;
        int allocationSize = checked(pageSize * 2);
        IntPtr allocation = NativeMemoryManager.Alloc(allocationSize, MemoryProtection.ReadWrite);
        Assert.NotEqual(IntPtr.Zero, allocation);

        try
        {
            IntPtr secondPage = allocation + pageSize;
            Assert.True(NativeMemoryManager.Protect(allocation, pageSize, MemoryProtection.Read, out _));
            Assert.True(NativeMemoryManager.Protect(secondPage, pageSize, MemoryProtection.ReadWrite, out _));

            Assert.False(NativeMemoryManager.Protect(
                allocation,
                allocationSize,
                MemoryProtection.ExecuteReadWrite,
                out _));

            Assert.Equal(MemoryProtection.Read, NativeMemoryManager.QueryProtection((UInt64)allocation));
            Assert.Equal(MemoryProtection.ReadWrite, NativeMemoryManager.QueryProtection((UInt64)secondPage));
        }
        finally
        {
            NativeMemoryManager.Free(allocation, allocationSize);
        }
    }

    [Fact]
    public void ThreadPatchScope_RestoresEveryTouchedPageIndependently()
    {
        Assert.SkipUnless(PlatformInfo.IsWindows, "The protected thread patcher is Win32-only.");

        int pageSize = PlatformInfo.PageSize;
        int allocationSize = checked(pageSize * 2);
        IntPtr allocation = NativeMemoryManager.Alloc(allocationSize, MemoryProtection.ReadWrite);
        Assert.NotEqual(IntPtr.Zero, allocation);

        try
        {
            IntPtr secondPage = allocation + pageSize;
            Assert.True(NativeMemoryManager.Protect(allocation, pageSize, MemoryProtection.Read, out _));
            Assert.True(NativeMemoryManager.Protect(secondPage, pageSize, MemoryProtection.ReadWrite, out _));

            UInt64 patchAddress = (UInt64)(secondPage - 3);
            InstructionPointerMap map = new(
                patchAddress,
                sourceLength: 5,
                sourceInstructionPointers: new UInt64[] { patchAddress },
                relocatedInstructionPointers: new UInt64[] { patchAddress });

            ThreadPatchScope.Apply(map, installing: false, patchAddress, new byte[5]);

            Assert.Equal(MemoryProtection.Read, NativeMemoryManager.QueryProtection((UInt64)allocation));
            Assert.Equal(MemoryProtection.ReadWrite, NativeMemoryManager.QueryProtection((UInt64)secondPage));
        }
        finally
        {
            NativeMemoryManager.Free(allocation, allocationSize);
        }
    }

    [Fact]
    public void CodePatch_PreservesRawWin32ProtectionModifiers()
    {
        Assert.SkipUnless(PlatformInfo.IsWindows, "Raw PAGE_* modifiers are Win32-only.");

        int pageSize = PlatformInfo.PageSize;
        IntPtr allocation = NativeMemoryManager.Alloc(pageSize, MemoryProtection.ReadWrite);
        Assert.NotEqual(IntPtr.Zero, allocation);

        const UInt32 PageReadWrite = 0x04;
        const UInt32 PageGuard = 0x100;
        NativeMethods.Win32MemoryPermissions ignored;

        try
        {
            Assert.True(NativeMethods.VirtualProtect(allocation, checked((UInt32)pageSize), (NativeMethods.Win32MemoryPermissions)(PageReadWrite | PageGuard), &ignored));

            CodePatch.Write((UInt64)allocation, new byte[] { 0x90 });

            NativeMethods.Win32MEMORY_BASIC_INFORMATION info = default;
            Assert.NotEqual(
                0,
                NativeMethods.VirtualQueryEx(
                    InstructionCache.CurrentProcessHandle,
                    allocation,
                    &info,
                    checked((UInt32)sizeof(NativeMethods.Win32MEMORY_BASIC_INFORMATION))));

            Assert.Equal(PageReadWrite | PageGuard, info.Protect);
            Assert.Equal(MemoryProtection.ReadWrite | MemoryProtection.Guard, NativeMemoryManager.QueryProtection((UInt64)allocation));
        }
        finally
        {
            NativeMethods.VirtualProtect(allocation, checked((UInt32)pageSize), NativeMethods.Win32MemoryPermissions.PAGE_READWRITE, &ignored);
            NativeMemoryManager.Free(allocation, pageSize);
        }
    }
}
