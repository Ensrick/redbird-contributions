using System;
using System.Buffers;

namespace RedBird.Core.Memory;

/// <summary>
/// A searchable region of memory together with the virtual address its first byte lives at.
/// </summary>
public sealed class ScanRegion : IDisposable
{
    private readonly IDisposable? _owner;
    private readonly ReadOnlyMemory<byte> _memory;

    private ScanRegion(ReadOnlyMemory<byte> memory, UInt64 baseAddress, IDisposable? owner, string description)
    {
        _memory = memory;
        _owner = owner;
        BaseAddress = baseAddress;
        Description = description;
    }

    /// <summary>Virtual address corresponding to offset 0 of this region.</summary>
    public UInt64 BaseAddress { get; }

    /// <summary>Region size in bytes.</summary>
    public int Length => _memory.Length;

    /// <summary>Diagnostic label, e.g. the module name.</summary>
    public string Description { get; }

    /// <summary>The region's bytes.</summary>
    public ReadOnlySpan<byte> Span => _memory.Span;

    /// <summary>The region's bytes, storable in a field.</summary>
    public ReadOnlyMemory<byte> Memory => _memory;

    /// <summary>Converts a region-relative offset into an absolute virtual address.</summary>
    public UInt64 ToAddress(long offset) => BaseAddress + (UInt64)offset;

    /// <summary>
    /// Wraps live memory without copying. The caller guarantees the pages stay mapped and readable for the lifetime of the region - which is trivially true for a loaded module.
    /// </summary>
    public static unsafe ScanRegion FromPointer(IntPtr address, int length, string? description = null)
    {
        if (address == IntPtr.Zero)
            throw new ArgumentNullException(nameof(address));
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        UnmanagedMemoryManager manager = new((byte*)address, length);
        return new ScanRegion(manager.Memory, (UInt64)address.ToInt64(), manager, description ?? $"0x{address.ToInt64():X16}+0x{length:X}");
    }

    /// <summary>
    /// Wraps a loaded PE module, reading <c>SizeOfImage</c> from its optional header so the caller does not have to know the module's extent.
    /// </summary>
    public static unsafe ScanRegion FromModule(IntPtr moduleHandle, string? description = null)
    {
        if (moduleHandle == IntPtr.Zero)
            throw new ArgumentNullException(nameof(moduleHandle));

        byte* baseAddress = (byte*)moduleHandle;

        // IMAGE_DOS_HEADER.e_magic == 'MZ'
        if (baseAddress[0] != 0x4D || baseAddress[1] != 0x5A)
            throw new BadImageFormatException("Module does not start with an MZ signature.");

        int ntOffset = *(int*)(baseAddress + 0x3C); // IMAGE_DOS_HEADER.e_lfanew
        byte* ntHeaders = baseAddress + ntOffset;

        if (*(uint*)ntHeaders != 0x0000_4550) // 'PE\0\0'
            throw new BadImageFormatException("Module has no PE signature.");

        // IMAGE_NT_HEADERS: Signature(4) + FileHeader(20) => OptionalHeader at +24.
        // IMAGE_OPTIONAL_HEADER(32|64).SizeOfImage sits at +56 in both variants.
        int sizeOfImage = *(int*)(ntHeaders + 24 + 56);
        if (sizeOfImage <= 0)
            throw new BadImageFormatException("Module reports a non-positive SizeOfImage.");

        return FromPointer(moduleHandle, sizeOfImage, description ?? $"module@0x{moduleHandle.ToInt64():X16}");
    }

    /// <summary>Wraps an existing buffer without copying.</summary>
    public static ScanRegion FromMemory(ReadOnlyMemory<byte> memory, UInt64 baseAddress, string? description = null)
        => new(memory, baseAddress, owner: null, description ?? "memory");

    /// <summary>
    /// Copies a span into a fresh array. Only use this when the source is transient; prefer <see cref="FromPointer"/> or <see cref="FromMemory"/>.
    /// </summary>
    public static ScanRegion Copy(ReadOnlySpan<byte> memory, UInt64 baseAddress, string? description = null)
        => new(memory.ToArray(), baseAddress, owner: null, description ?? "copy");

    /// <inheritdoc/>
    public void Dispose() => _owner?.Dispose();

    /// <inheritdoc/>
    public override string ToString() => $"{Description} (0x{BaseAddress:X16}, {Length} bytes)";

    /// <summary>
    /// Exposes a raw pointer as <see cref="ReadOnlyMemory{T}"/>. 
    /// Standard <see cref="MemoryManager{T}"/> pattern: nothing is pinned because the memory was never managed to begin with.
    /// </summary>
    private sealed unsafe class UnmanagedMemoryManager(byte* pointer, int length) : MemoryManager<byte>
    {
        private readonly byte* _pointer = pointer;
        private readonly int _length = length;

        public override Span<byte> GetSpan() => new(_pointer, _length);

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if ((uint)elementIndex > (uint)_length)
                throw new ArgumentOutOfRangeException(nameof(elementIndex));

            return new MemoryHandle(_pointer + elementIndex);
        }

        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }
    }
}
