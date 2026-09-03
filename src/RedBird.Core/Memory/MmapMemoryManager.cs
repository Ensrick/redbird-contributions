using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RedBird.Core.PInvoke;

namespace RedBird.Core.Memory;

/// <summary>
/// Manages a large, contiguous block of unmanaged virtual memory on Linux using <c>mmap</c>/<c>mprotect</c>/<c>munmap</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the Linux counterpart to <see cref="VirtualAllocManager"/> and implements the same <see cref="IMemoryManager"/> contract, so the two are interchangeable from the dispatcher's
/// point of view. It reserves a region of address space up front with <c>PROT_NONE</c> (analogous to Windows' <c>MEM_RESERVE</c>) and then uses <c>mprotect</c> to grant access to sub-ranges as
/// they are allocated (analogous to <c>MEM_COMMIT</c>), and revokes access again on <see cref="Free"/>.
/// </para>
/// </remarks>
public unsafe sealed class MmapMemoryManager : IMemoryManager
{
    private const ulong DefaultPoolSize = 4 * 1024 * 1024;

    private static readonly Lazy<MmapMemoryManager> lazy = new(() => new MmapMemoryManager(DefaultPoolSize));

    /// <summary>
    /// Gets the singleton instance of the <see cref="MmapMemoryManager"/>.
    /// </summary>
    public static MmapMemoryManager Instance => lazy.Value;

    private readonly object _lock = new();
    private readonly IntPtr _baseAddress;
    private readonly ulong _totalSize;
    private readonly LinkedList<MemoryBlock> _memoryBlocks;
    private readonly ILogger? _logger;
    private bool _disposed;

    private sealed class MemoryBlock(IntPtr address, ulong size)
    {
        public IntPtr Address { get; } = address;
        public ulong Size { get; set; } = size;
        public bool IsFree { get; set; } = true;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MmapMemoryManager"/> class, reserving <paramref name="size"/>
    /// bytes of address space via an anonymous, inaccessible (<c>PROT_NONE</c>) <c>mmap</c> mapping.
    /// </summary>
    /// <param name="size">The total number of bytes to reserve.</param>
    /// <param name="logger">An optional logger for diagnostics.</param>
    /// <exception cref="OutOfMemoryException">Thrown if the initial reservation fails.</exception>
    public MmapMemoryManager(ulong size, ILogger? logger = null)
    {
        _logger = logger;

        IntPtr result = NativeMethods.mmap(IntPtr.Zero, checked((UIntPtr)size), NativeMethods.PROT_NONE, NativeMethods.MAP_PRIVATE | NativeMethods.MAP_ANONYMOUS, -1, 0);
        if (result == IntPtr.Zero || result == new IntPtr(-1))
        {
            throw new OutOfMemoryException($"Failed to reserve virtual memory via mmap. errno: {Marshal.GetLastWin32Error()}");
        }

        _baseAddress = result;
        _totalSize = size;
        _memoryBlocks = new LinkedList<MemoryBlock>();
        _memoryBlocks.AddFirst(new MemoryBlock(_baseAddress, _totalSize));
    }

    /// <inheritdoc/>
    public IntPtr Allocate(ulong size, MemoryProtection protection = MemoryProtection.ReadWrite)
    {
        if (size == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Allocation size cannot be zero.");
        }

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MmapMemoryManager));
        }

        lock (_lock)
        {
            LinkedListNode<MemoryBlock>? currentNode = _memoryBlocks.First;
            while (currentNode != null)
            {
                MemoryBlock block = currentNode.Value;
                if (block.IsFree && block.Size >= size)
                {
                    if (block.Size > size)
                    {
                        MemoryBlock newBlock = new(new IntPtr(block.Address.ToInt64() + (long)size), block.Size - size);
                        block.Size = size;
                        _memoryBlocks.AddAfter(currentNode, newBlock);
                    }

                    block.IsFree = false;

                    int prot = protection.ToPosixProtection();
                    int mprotectResult = NativeMethods.mprotect(block.Address, checked((UIntPtr)size), prot);
                    if (mprotectResult != 0)
                    {
                        block.IsFree = true;
                        LinkedListNode<MemoryBlock>? nextNode = currentNode.Next;
                        if (nextNode != null && nextNode.Value.Address == new IntPtr(block.Address.ToInt64() + (long)block.Size))
                        {
                            block.Size += nextNode.Value.Size;
                            _memoryBlocks.Remove(nextNode);
                        }
                        throw new OutOfMemoryException($"Failed to mprotect memory. errno: {Marshal.GetLastWin32Error()}");
                    }

                    return block.Address;
                }
                currentNode = currentNode.Next;
            }

            throw new OutOfMemoryException("Not enough contiguous unmanaged memory available to allocate.");
        }
    }

    /// <inheritdoc/>
    public void Free(IntPtr address)
    {
        lock (_lock)
        {
            LinkedListNode<MemoryBlock>? currentNode = _memoryBlocks.First;
            while (currentNode != null)
            {
                MemoryBlock block = currentNode.Value;
                if (block.Address == address)
                {
                    if (block.IsFree)
                    {
                        return;
                    }

                    block.IsFree = true;

                    // Revoke access; this is the mprotect analogue of MEM_DECOMMIT.
                    NativeMethods.mprotect(block.Address, checked((UIntPtr)block.Size), NativeMethods.PROT_NONE);

                    LinkedListNode<MemoryBlock>? nextNode = currentNode.Next;
                    if (nextNode != null && nextNode.Value.IsFree)
                    {
                        block.Size += nextNode.Value.Size;
                        _memoryBlocks.Remove(nextNode);
                    }

                    LinkedListNode<MemoryBlock>? prevNode = currentNode.Previous;
                    if (prevNode != null && prevNode.Value.IsFree)
                    {
                        prevNode.Value.Size += block.Size;
                        _memoryBlocks.Remove(currentNode);
                    }

                    return;
                }
                currentNode = currentNode.Next;
            }
        }
    }

    /// <summary>
    /// Releases the entire reserved region of virtual memory back to the operating system via <c>munmap</c>.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed && _baseAddress != IntPtr.Zero)
        {
            NativeMethods.munmap(_baseAddress, checked((UIntPtr)_totalSize));
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizer for the <see cref="MmapMemoryManager"/> class.
    /// </summary>
    ~MmapMemoryManager()
    {
        Dispose();
    }
}
