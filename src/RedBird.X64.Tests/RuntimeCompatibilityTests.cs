using System;
using Xunit;
using RedBird.Core.Memory;

namespace RedBird.X64.Tests;

/// <summary>Runtime compatibility paths used by Unity/Mono hosts.</summary>
public sealed class RuntimeCompatibilityTests
{
    [Fact]
    public void UnsupportedNoGcRegion_UsesCompatibilityPath()
    {
        bool ownsRegion = ThreadPatchScope.TryEnterNoGcRegionForPatch(
            static _ => throw new NotImplementedException());

        Assert.False(ownsRegion);
    }

    [Fact]
    public void ImplementedNoGcRegionWhichCannotStart_RemainsAFailure()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            ThreadPatchScope.TryEnterNoGcRegionForPatch(static _ => false));

        Assert.Contains("Could not enter a no-GC region", exception.Message, StringComparison.Ordinal);
    }
}
