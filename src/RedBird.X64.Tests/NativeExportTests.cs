using System;
using Xunit;
using RedBird.Core.PInvoke;
using RedBird.Core.Platform;
using RedBird.Core.Utilities;

namespace RedBird.X64.Tests;

public sealed class NativeExportTests
{
    [Fact]
    public void WindowsModuleHandle_ResolvesAnExportThroughTheMonoCompatiblePath()
    {
        Assert.SkipUnless(PlatformInfo.IsWindows, "This regression exercises GetProcAddress on Windows.");

        IntPtr kernel32 = WindowsMemoryMethods.GetModuleHandle("kernel32.dll");
        Assert.NotEqual(IntPtr.Zero, kernel32);

        Assert.True(NativeExports.TryResolve(kernel32, "GetCurrentProcessId", out UInt64 address));
        Assert.NotEqual(0UL, address);
    }
}
