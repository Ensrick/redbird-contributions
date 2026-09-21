using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RedBird.Core.Memory;

/// <summary>
/// Owns a stable, read/write native-memory block whose values can be updated from managed code and read directly by generated machine code.
/// </summary>
/// <remarks>
/// <para>
/// State is kept out of executable stub pages. 
/// This avoids changing code-page protection whenever a setting changes and works on hosts that enforce W^X
/// </para>
/// <para>
/// The block must outlive every native stub that embeds one of its addresses. 
/// Dispose it only after those hooks have been removed. 
/// </para>
/// </remarks>
public sealed unsafe class NativeStateBlock : IDisposable
{
    private const int DefaultCapacity = 4096;

    private readonly object _gate = new();
    private IntPtr _baseAddress;
    private int _bytesUsed;

    /// <summary>Allocates a zero-initialized state block.</summary>
    /// <param name="capacityBytes">Maximum number of bytes that can be reserved from the block.</param>
    public NativeStateBlock(int capacityBytes = DefaultCapacity)
    {
        if (capacityBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes));

        _baseAddress = Marshal.AllocHGlobal(capacityBytes);
        if (_baseAddress == IntPtr.Zero)
            throw new OutOfMemoryException($"Could not allocate a {capacityBytes}-byte native state block.");

        Capacity = capacityBytes;
        new Span<byte>((void*)_baseAddress, capacityBytes).Clear();
    }

    /// <summary>Maximum number of bytes that can be reserved from this block.</summary>
    public int Capacity { get; }

    /// <summary>Number of bytes occupied, including alignment padding.</summary>
    public int BytesUsed
    {
        get
        {
            lock (_gate)
                return _bytesUsed;
        }
    }

    /// <summary>True after the block has been released.</summary>
    public bool IsDisposed => _baseAddress == IntPtr.Zero;

    /// <summary>Base address of the block.</summary>
    public UInt64 Address => GetAddress(0, 0);

    /// <summary>Reserves a single unmanaged value.</summary>
    public NativeStateValue<T> AllocateValue<T>(T initialValue = default) where T : unmanaged
    {
        int offset = Reserve(sizeof(T), AlignmentOf<T>());
        NativeStateValue<T> value = new(this, offset);
        value.Value = initialValue;
        return value;
    }

    /// <summary>Reserves a contiguous array of unmanaged values.</summary>
    public NativeStateArray<T> AllocateArray<T>(int length, T initialValue = default) where T : unmanaged
    {
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        int byteCount = checked(sizeof(T) * length);
        int offset = Reserve(byteCount, AlignmentOf<T>());
        NativeStateArray<T> values = new(this, offset, length);
        values.Fill(initialValue);
        return values;
    }

    /// <summary>
    /// Reserves a byte-backed Boolean. The native representation is always exactly zero or one.
    /// </summary>
    public NativeFlag AllocateFlag(bool initialValue = false)
    {
        int offset = Reserve(1, 1);
        NativeFlag flag = new(this, offset);
        flag.Value = initialValue;
        return flag;
    }

    /// <summary>
    /// Reserves a tightly packed Boolean array with one byte per element. 
    /// Every element is stored as exactly zero or one, making it directly usable with an x86/x64 byte operand.
    /// </summary>
    public NativeFlagArray AllocateFlags(int length, bool initialValue = false)
    {
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        int offset = Reserve(length, 1);
        NativeFlagArray flags = new(this, offset, length);
        flags.Fill(initialValue);
        return flags;
    }

    /// <summary>Releases the native block.</summary>
    /// <remarks>All hooks that reference this block must be removed before calling this method.</remarks>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_baseAddress == IntPtr.Zero)
                return;

            Marshal.FreeHGlobal(_baseAddress);
            _baseAddress = IntPtr.Zero;
            _bytesUsed = 0;
        }

        GC.SuppressFinalize(this);
    }

    internal byte* GetPointer(int offset, int byteCount)
    {
        IntPtr baseAddress = _baseAddress;
        if (baseAddress == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(NativeStateBlock));
        if (offset < 0 || byteCount < 0 || offset > Capacity - byteCount)
            throw new ArgumentOutOfRangeException(nameof(offset));

        return (byte*)baseAddress + offset;
    }

    internal UInt64 GetAddress(int offset, int byteCount) => PointerToUInt64((IntPtr)GetPointer(offset, byteCount));

    private int Reserve(int byteCount, int alignment)
    {
        lock (_gate)
        {
            if (_baseAddress == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(NativeStateBlock));

            int padding = (alignment - (_bytesUsed & (alignment - 1))) & (alignment - 1);
            int offset = checked(_bytesUsed + padding);
            int end = checked(offset + byteCount);

            if (end > Capacity)
            {
                throw new InvalidOperationException($"The native state block needs {end} bytes, but its capacity is {Capacity} bytes.");
            }

            _bytesUsed = end;
            return offset;
        }
    }

    private static int AlignmentOf<T>() where T : unmanaged
    {
        int size = sizeof(T);
        int alignment = 1;
        int maximum = Math.Min(size, IntPtr.Size);

        while (alignment <= maximum / 2)
            alignment <<= 1;

        return alignment;
    }

    private static UInt64 PointerToUInt64(IntPtr pointer) => IntPtr.Size == sizeof(UInt32) ? unchecked((UInt32)pointer.ToInt32()) : unchecked((UInt64)pointer.ToInt64());
}

/// <summary>A managed view over one value stored in a <see cref="NativeStateBlock"/>.</summary>
public sealed unsafe class NativeStateValue<T> where T : unmanaged
{
    private readonly NativeStateBlock _owner;
    private readonly int _offset;

    internal NativeStateValue(NativeStateBlock owner, int offset)
    {
        _owner = owner;
        _offset = offset;
    }

    /// <summary>Absolute native address to embed in generated assembly.</summary>
    public UInt64 Address => _owner.GetAddress(_offset, sizeof(T));

    /// <summary>Reads or writes the native value.</summary>
    public T Value
    {
        get => *(T*)_owner.GetPointer(_offset, sizeof(T));
        set => *(T*)_owner.GetPointer(_offset, sizeof(T)) = value;
    }
}

/// <summary>A managed view over a contiguous array stored in a <see cref="NativeStateBlock"/>.</summary>
public sealed unsafe class NativeStateArray<T> where T : unmanaged
{
    private readonly NativeStateBlock _owner;
    private readonly int _offset;

    internal NativeStateArray(NativeStateBlock owner, int offset, int length)
    {
        _owner = owner;
        _offset = offset;
        Length = length;
    }

    /// <summary>Number of elements.</summary>
    public int Length { get; }

    /// <summary>Absolute native address of the first element.</summary>
    public UInt64 Address => _owner.GetAddress(_offset, checked(sizeof(T) * Length));

    /// <summary>Reads or writes an element.</summary>
    public T this[int index]
    {
        get => *GetElementPointer(index);
        set => *GetElementPointer(index) = value;
    }

    /// <summary>Returns the absolute native address of one element.</summary>
    public UInt64 GetAddress(int index)
    {
        ValidateIndex(index);
        return _owner.GetAddress(checked(_offset + (index * sizeof(T))), sizeof(T));
    }

    /// <summary>Fills the complete array.</summary>
    public void Fill(T value) => AsSpan().Fill(value);

    /// <summary>
    /// Exposes the native array as a span. The span must not be retained after the owning block is
    /// disposed.
    /// </summary>
    public Span<T> AsSpan()
        => new(_owner.GetPointer(_offset, checked(sizeof(T) * Length)), Length);

    private T* GetElementPointer(int index)
    {
        ValidateIndex(index);
        return (T*)_owner.GetPointer(checked(_offset + (index * sizeof(T))), sizeof(T));
    }

    private void ValidateIndex(int index)
    {
        if ((UInt32)index >= (UInt32)Length)
            throw new ArgumentOutOfRangeException(nameof(index));
    }
}

/// <summary>
/// A byte-backed Boolean stored in native memory for direct reads from generated assembly.
/// </summary>
public sealed unsafe class NativeFlag
{
    private readonly NativeStateBlock _owner;
    private readonly int _offset;

    internal NativeFlag(NativeStateBlock owner, int offset)
    {
        _owner = owner;
        _offset = offset;
    }

    /// <summary>Absolute address of the one-byte flag.</summary>
    public UInt64 Address => _owner.GetAddress(_offset, 1);

    /// <summary>
    /// Gets or sets the flag with acquire/release semantics. The stored byte is always zero or one.
    /// </summary>
    public bool Value
    {
        get => Volatile.Read(ref *_owner.GetPointer(_offset, 1)) != 0;
        set => Volatile.Write(ref *_owner.GetPointer(_offset, 1), value ? (byte)1 : (byte)0);
    }
}

/// <summary>
/// A contiguous byte-backed Boolean array stored in native memory for indexed assembly reads.
/// </summary>
public sealed unsafe class NativeFlagArray
{
    private readonly NativeStateBlock _owner;
    private readonly int _offset;

    internal NativeFlagArray(NativeStateBlock owner, int offset, int length)
    {
        _owner = owner;
        _offset = offset;
        Length = length;
    }

    /// <summary>Number of flags. Each flag occupies one byte.</summary>
    public int Length { get; }

    /// <summary>Absolute address of the first flag.</summary>
    public UInt64 Address => _owner.GetAddress(_offset, Length);

    /// <summary>Gets or sets a flag with acquire/release semantics.</summary>
    public bool this[int index]
    {
        get => Volatile.Read(ref *GetElementPointer(index)) != 0;
        set => Volatile.Write(ref *GetElementPointer(index), value ? (byte)1 : (byte)0);
    }

    /// <summary>Returns the absolute native address of one flag.</summary>
    public UInt64 GetAddress(int index)
    {
        ValidateIndex(index);
        return _owner.GetAddress(_offset + index, 1);
    }

    /// <summary>Sets every flag.</summary>
    public void Fill(bool value)
    {
        byte stored = value ? (byte)1 : (byte)0;
        for (int index = 0; index < Length; index++)
            Volatile.Write(ref *GetElementPointer(index), stored);
    }

    private byte* GetElementPointer(int index)
    {
        ValidateIndex(index);
        return _owner.GetPointer(_offset + index, 1);
    }

    private void ValidateIndex(int index)
    {
        if ((UInt32)index >= (UInt32)Length)
            throw new ArgumentOutOfRangeException(nameof(index));
    }
}
