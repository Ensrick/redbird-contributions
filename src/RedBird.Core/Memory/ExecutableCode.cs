using System;
using System.IO;
using System.Runtime.InteropServices;
using Iced.Intel;

namespace RedBird.Core.Memory;

/// <summary>
/// A block of machine code assembled into executable memory at run time, callable from managed code through a delegate.
/// </summary>
/// <remarks>
/// <para>
/// This is the mechanism behind defining a native function inline: pick a bitness, emit a body with an Iced <see cref="Assembler"/>, and the result is a real function at a real address that
/// a hooking engine can patch and a delegate can invoke. The architecture packages derive the usable types from this: <c>RedBird.X64.Assembly.ExecutableFunction</c> for 64-bit bodies
/// and <c>RedBird.X86.Assembly.ExecutableX86Function</c> for 32-bit ones: only the bitness and the addressing constraints differ.
/// </para>
/// <para>
/// The region is filled with <c>0xCC</c> before the body is written. An <c>int3</c> is both a clear function terminator for a prologue analyser and an immediate, unmistakable crash if
/// control ever runs off the end of the body.
/// </para>
/// <para>
/// One page is reserved per function. The first half holds the code; a scratch data slot lives at <see cref="DataAddress"/> in the second half, far enough from the entry point to be a realistic
/// RIP-relative operand yet close enough to stay in range.
/// </para>
/// </remarks>
public abstract unsafe class ExecutableCode : IDisposable
{
    /// <summary>Bytes reserved per function.</summary>
    protected const int RegionSize = 4096;

    /// <summary>Offset within the region at which <see cref="DataAddress"/> points.</summary>
    protected const int DataOffset = 2048;

    private readonly IntPtr _region;
    private bool _disposed;

    /// <summary>Assembles a body into a fresh executable region.</summary>
    /// <param name="bitness">16, 32 or 64 - the mode the body is assembled for.</param>
    /// <param name="requires32BitAddress">
    /// True for 32-bit bodies, whose <see cref="Address"/> must be expressible as a 32-bit value.
    /// </param>
    /// <param name="build">Emits the body. The assembler is positioned at <see cref="Address"/>.</param>
    /// <param name="configure">Optional hook to seed the data slot before the code is written.</param>
    protected ExecutableCode(int bitness, bool requires32BitAddress, Action<Assembler, ExecutableCodeLayout> build, Action<ExecutableCodeLayout>? configure)
    {
        if (build is null)
            throw new ArgumentNullException(nameof(build));

        IntPtr region = Allocate(out bool writeExecute);
        if (region == IntPtr.Zero)
            throw new InvalidOperationException("Could not allocate executable memory for a runtime-assembled function.");

        try
        {
            UInt64 baseAddress = unchecked((UInt64)region.ToInt64());
            if (requires32BitAddress && baseAddress > UInt32.MaxValue)
            {
                throw new PlatformNotSupportedException($"A 32-bit function was allocated at 0x{baseAddress:X16}, which no 32-bit code can address. Assemble 32-bit bodies in a 32-bit process.");
            }

            new Span<byte>((void*)region, RegionSize).Fill(0xCC);

            ExecutableCodeLayout layout = new(baseAddress, baseAddress + DataOffset);

            // Seeding happens before the write-protect step below, so a data slot can be set up even on a host that refuses write+execute.
            configure?.Invoke(layout);

            Assembler assembler = new(bitness);
            build(assembler, layout);

            using MemoryStream stream = new();
            if (!assembler.TryAssemble(new StreamCodeWriter(stream), baseAddress, out string? error, out _))
                throw new InvalidOperationException($"Could not assemble the runtime function: {error}");

            byte[] code = stream.ToArray();
            if (code.Length > DataOffset)
            {
                throw new InvalidOperationException($"The assembled body is {code.Length} bytes, which runs into the data slot at +{DataOffset}.");
            }

            Marshal.Copy(code, 0, region, code.Length);

            // Only needed when the allocator had to fall back to a writable mapping: promoting an
            // already-RWX region would also make the data slot read-only, and it shares the page.
            if (!writeExecute && !NativeMemoryManager.FinalizeStub(region, RegionSize))
                throw new InvalidOperationException("Could not make the runtime function executable.");

            InstructionCache.Flush(baseAddress, code.Length);

            _region = region;
            Address = baseAddress;
            CodeLength = code.Length;
            OriginalBytes = code;
            IsWritable = writeExecute;
        }
        catch
        {
            NativeMemoryManager.Free(region, RegionSize);
            throw;
        }
    }

    /// <summary>Entry point of the assembled function.</summary>
    public UInt64 Address { get; }

    /// <summary>Entry point as a pointer, for APIs that take one.</summary>
    public IntPtr EntryPoint => _region;

    /// <summary>Length of the assembled body, including any padding the builder emitted.</summary>
    public int CodeLength { get; }

    /// <summary>Snapshot of the body as assembled, before anything patched it.</summary>
    public byte[] OriginalBytes { get; }

    /// <summary>
    /// True when the region is still writable, which is the normal case. A host that refuses
    /// write+execute forces the region read-execute once the code is in, and
    /// <see cref="DataValue"/> can then only be set through the seeding callback.
    /// </summary>
    public bool IsWritable { get; }

    /// <summary>
    /// A 32-bit scratch slot inside the same region, far enough from the code to be reachable by a
    /// RIP-relative operand.
    /// </summary>
    public UInt64 DataAddress => Address + DataOffset;

    /// <summary>Reads or writes the value in the scratch slot.</summary>
    /// <exception cref="InvalidOperationException">The region is no longer writable.</exception>
    public int DataValue
    {
        get
        {
            ThrowIfDisposed();
            return *(int*)((byte*)_region + DataOffset);
        }
        set
        {
            ThrowIfDisposed();
            if (!IsWritable)
            {
                throw new InvalidOperationException("This region is read-execute, so its data slot can only be seeded while the function is being created.");
            }

            *(int*)((byte*)_region + DataOffset) = value;
        }
    }

    /// <summary>Marshals a delegate that calls this function.</summary>
    public TDelegate GetDelegate<TDelegate>() where TDelegate : Delegate
    {
        ThrowIfDisposed();
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(_region);
    }

    /// <summary>Reads back the current bytes at the entry point, to compare against <see cref="OriginalBytes"/>.</summary>
    public byte[] ReadCurrentBytes(int length)
    {
        ThrowIfDisposed();
        if (length < 0 || length > RegionSize)
            throw new ArgumentOutOfRangeException(nameof(length));

        byte[] bytes = new byte[length];
        Marshal.Copy(_region, bytes, 0, length);
        return bytes;
    }

    /// <summary>True when the first <paramref name="length"/> bytes still match what was assembled.</summary>
    public bool MatchesOriginal(int length)
    {
        if (length > OriginalBytes.Length)
            throw new ArgumentOutOfRangeException(nameof(length), "Only the assembled body was snapshotted.");

        byte[] current = ReadCurrentBytes(length);
        for (int index = 0; index < length; index++)
        {
            if (current[index] != OriginalBytes[index])
                return false;
        }

        return true;
    }

    /// <summary>Releases the region.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        NativeMemoryManager.Free(_region, RegionSize);
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);
    }

    private static IntPtr Allocate(out bool writeExecute)
    {
        IntPtr region = NativeMemoryManager.Alloc(RegionSize, MemoryProtection.ExecuteReadWrite);
        if (region != IntPtr.Zero)
        {
            writeExecute = true;
            return region;
        }

        // W^X hosts refuse write+execute; take it writable and promote it once the code is in.
        writeExecute = false;
        return NativeMemoryManager.Alloc(RegionSize, MemoryProtection.ReadWrite);
    }
}
