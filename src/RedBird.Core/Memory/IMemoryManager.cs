using System;

namespace RedBird.Core.Memory;

/// <summary>
/// Abstraction over an unmanaged executable-memory allocator.
/// </summary>
public interface IMemoryManager : IDisposable
{
    /// <summary>
    /// Allocates a block of unmanaged memory with the requested protection.
    /// </summary>
    /// <param name="size">The number of bytes to allocate.</param>
    /// <param name="protection">The memory protection to apply to the allocated region.</param>
    /// <returns>A pointer to the base of the allocated block.</returns>
    IntPtr Allocate(ulong size, MemoryProtection protection = MemoryProtection.ReadWrite);

    /// <summary>
    /// Frees a block of memory that was previously returned by <see cref="Allocate"/>.
    /// </summary>
    /// <param name="address">The address of the block to free.</param>
    void Free(IntPtr address);
}
