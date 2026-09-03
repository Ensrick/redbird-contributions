using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RedBird.Core.PInvoke;

namespace RedBird.Core.Memory;

/// <summary>
/// Manages a large, contiguous block of unmanaged virtual memory using the Windows VirtualAlloc API.
/// </summary>
/// <remarks>
/// <para>
/// This class acts as a high-performance memory pool for unmanaged memory operations. 
/// It reserves a significant chunk of virtual address space upon initialization and then commits or decommits pages from this space on demand. 
/// This approach reduces the overhead of frequent calls to the operating system for small memory allocations and helps to prevent memory fragmentation within the managed heap, making it
/// decent for applications that require frequent allocation/deallocation of unmanaged memory buffers..
/// </para>
/// <para>
/// This is the Windows implementation of <see cref="IMemoryManager"/>. On Linux, <see cref="MmapMemoryManager"/> serves the equivalent role using <c>mmap</c>/<c>mprotect</c>/<c>munmap</c>. 
/// Both are selected automatically by <see cref="DefaultMemoryManager"/> based on the current platform, or you can construct and pass either (or your own <see cref="IMemoryManager"/> implementation).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// <![CDATA[
/// public static unsafe void Main(string[] args)
/// {
///     try
///     {
///         // Request a 1KB block from the singleton instance
///         IntPtr block1 = VirtualAllocManager.Instance.Allocate(1024);
///         Console.WriteLine($"Allocated 1KB at: {block1.ToInt64():X}");
///
///         // Write to and read from the memory
///         int* pData = (int*)block1;
///         *pData = 12345;
///         Console.WriteLine($"Value written: {*pData}");
///
///         // Free the block when done
///         VirtualAllocManager.Instance.Free(block1);
///         Console.WriteLine("Memory block freed.");
///     }
///     catch (Exception ex)
///     {
///         Console.WriteLine($"An error occurred: {ex.Message}");
///     }
/// }
/// ]]>
/// </code>
/// </example>
public sealed class VirtualAllocManager : IMemoryManager
{
    /// <summary>
    /// The default total size of the virtual memory pool (4 MB) for the singleton instance.
    /// </summary>
    private const ulong DefaultPoolSize = 4 * 1024 * 1024;

    private static readonly Lazy<VirtualAllocManager> lazy = new(() => new VirtualAllocManager(DefaultPoolSize));

    /// <summary>
    /// Gets the singleton instance of the <see cref="VirtualAllocManager"/>.
    /// </summary>
    /// <remarks>
    /// The instance is created in a thread-safe manner using lazy initialization, ensuring it is only instantiated upon first access.
    /// </remarks>
    public static VirtualAllocManager Instance { get { return lazy.Value; } }

    private readonly IntPtr _baseAddress;
    private readonly ulong _totalSize;
    private readonly LinkedList<MemoryBlock> _memoryBlocks;

    private class MemoryBlock(IntPtr address, ulong size)
    {
        public IntPtr Address { get; } = address;
        public ulong Size { get; set; } = size;
        public bool IsFree { get; set; } = true;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VirtualAllocManager"/> class by reserving a block of virtual memory.
    /// </summary>
    /// <param name="size">The total number of bytes to reserve in the virtual address space.</param>
    /// <param name="permission">The desired memory permission for the page.</param>
    /// <exception cref="OutOfMemoryException">Thrown if the initial virtual memory reservation fails.</exception>
    public VirtualAllocManager(ulong size, NativeMethods.Win32MemoryPermissions permission = NativeMethods.Win32MemoryPermissions.PAGE_READWRITE)
    {
        unsafe
        {
            _baseAddress = NativeMethods.VirtualAlloc(IntPtr.Zero, checked((UIntPtr)size), NativeMethods.Win32AllocationType.MEM_RESERVE, permission);
        }
        if (_baseAddress == IntPtr.Zero)
        {
            throw new OutOfMemoryException($"Failed to reserve virtual memory. Error code: {Marshal.GetLastWin32Error()}");
        }

        _totalSize = size;
        _memoryBlocks = new LinkedList<MemoryBlock>();
        _memoryBlocks.AddFirst(new MemoryBlock(_baseAddress, _totalSize));
    }

    /// <summary>
    /// Allocates a block of unmanaged memory from the pool.
    /// </summary>
    /// <param name="size">The number of bytes to allocate.</param>
    /// <param name="protection">The desired memory protection for the page, translated internally into the equivalent Win32 PAGE_* flag.</param>
    /// <returns>An <see cref="IntPtr"/> to the base address of the allocated memory block.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="size"/> is zero.</exception>
    /// <exception cref="OutOfMemoryException">Thrown if no sufficiently large contiguous block is available in the pool, or if the memory commit fails.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if the memory manager has been disposed.</exception>
    /// <remarks>
    /// This method finds a suitable free block within the reserved virtual address space. 
    /// If a perfect match is not found, a larger block may be split. It then commits the physical memory for the requested size using <c>VirtualAlloc</c>, making it available for read/write operations.
    /// </remarks>
    public IntPtr Allocate(ulong size, MemoryProtection protection = MemoryProtection.ReadWrite)
    {
        if (size == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Allocation size cannot be zero.");
        }

        NativeMethods.Win32MemoryPermissions permission = protection.ToWindowsProtection();

        LinkedListNode<MemoryBlock>? currentNode = _memoryBlocks.First;
        while (currentNode != null)
        {
            MemoryBlock block = currentNode.Value;
            if (block.IsFree && block.Size >= size)
            {
                if (block.Size > size)
                {
                    // Split the block
                    MemoryBlock newBlock = new(new IntPtr(block.Address.ToInt64() + (long)size), block.Size - size);
                    block.Size = size;
                    _memoryBlocks.AddAfter(currentNode, newBlock);
                }

                block.IsFree = false;

                // Commit the memory
                unsafe
                {
                    if (NativeMethods.VirtualAlloc(block.Address, checked((UIntPtr)size), NativeMethods.Win32AllocationType.MEM_COMMIT, permission) == IntPtr.Zero)
                    {
                        // Allocation failed, revert the block state
                        block.IsFree = true;

                        // Attempt to merge with the next block if it was just split
                        LinkedListNode<MemoryBlock>? nextNode = currentNode.Next;
                        if (nextNode != null && nextNode.Value.Address == new IntPtr(block.Address.ToInt64() + (long)block.Size))
                        {
                            block.Size += nextNode.Value.Size;
                            _memoryBlocks.Remove(nextNode);
                        }
                        throw new OutOfMemoryException($"Failed to commit memory. Error code: {Marshal.GetLastWin32Error()}");
                    }
                }
                return block.Address;
            }
            currentNode = currentNode.Next;
        }

        throw new OutOfMemoryException("Not enough contiguous unmanaged memory available to allocate.");
    }

    /// <summary>
    /// Frees a previously allocated block of memory, returning it to the pool.
    /// </summary>
    /// <param name="address">The pointer to the memory block, previously returned by <see cref="Allocate"/>.</param>
    /// <exception cref="ObjectDisposedException">Thrown if the memory manager has been disposed.</exception>
    /// <remarks>
    /// This method decommits the physical memory associated with the block via <c>VirtualFree</c>, releasing it back to the operating system. The virtual address space remains reserved. 
    /// The method will also attempt to merge the freed block with adjacent free blocks to reduce fragmentation within the memory pool.
    /// </remarks>
    public void Free(IntPtr address)
    {
        if (address == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(address), "Address cannot be null.");
        }

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

                // Decommit the memory
                unsafe
                {
                    NativeMethods.VirtualFree(block.Address, checked((UIntPtr)block.Size), (uint)NativeMethods.Win32AllocationType.MEM_DECOMMIT);
                }

                // Attempt to merge with the next block
                LinkedListNode<MemoryBlock>? nextNode = currentNode.Next;
                if (nextNode != null && nextNode.Value.IsFree)
                {
                    block.Size += nextNode.Value.Size;
                    _memoryBlocks.Remove(nextNode);
                }

                // Attempt to merge with the previous block
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

    /// <summary>
    /// Releases the entire reserved region of virtual memory back to the operating system.
    /// </summary>
    /// <remarks>
    /// Once disposed, the memory manager can no longer be used. For the singleton <see cref="Instance"/>, this is typically called automatically by the finalizer when the application domain unloads.
    /// </remarks>
    public void Dispose()
    {
        if (_baseAddress != IntPtr.Zero)
        {
            unsafe
            {
                NativeMethods.VirtualFree(_baseAddress, UIntPtr.Zero, (uint)NativeMethods.Win32AllocationType.MEM_RELEASE);
            }
        }
        GC.SuppressFinalize(this);
    }


    /// <summary>
    /// Fill out a region of memory with a specified value
    /// </summary>
    /// <param name="ptr">The region to write to</param>
    /// <param name="length">Size of the region</param>
    /// <param name="value">The value to pad the area out with</param>
    public static void FillMemory(IntPtr ptr, int length, byte value)
    {
        unsafe
        {
            if (ptr == IntPtr.Zero)
            {
                throw new ArgumentNullException(nameof(ptr), "Pointer cannot be null.");
            }
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length), "Length cannot be negative.");
            }
            Span<byte> span = new(ptr.ToPointer(), length);
            span.Fill(value);
        }
    }

    /// <summary>
    /// Finalizer for the <see cref="VirtualAllocManager"/> class.
    /// </summary>
    ~VirtualAllocManager()
    {
        Dispose();
    }
}
