using System;
using System.Runtime.InteropServices;

namespace RedBird.Core.Memory;

/// <summary>
/// Resolves the default <see cref="IMemoryManager"/> singleton for the current platform.
/// </summary>
public static class DefaultMemoryManager
{
    private static readonly Lazy<IMemoryManager> _instance = new(() => Resolve());

    /// <summary>
    /// Gets the default, platform-appropriate <see cref="IMemoryManager"/> instance.
    /// </summary>
    public static IMemoryManager Instance => _instance.Value;

    private static IMemoryManager Resolve()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return VirtualAllocManager.Instance;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return MmapMemoryManager.Instance;
        }

        throw new PlatformNotSupportedException("No default IMemoryManager is available for this platform. Supply your own IMemoryManager implementation to the dispatcher instead.");
    }
}
