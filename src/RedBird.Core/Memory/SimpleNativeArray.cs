using System;
using System.Runtime.CompilerServices;

namespace RedBird.Core.Memory;
public readonly unsafe struct SimpleNativeArray<T> : IDisposable where T : unmanaged
{
    public readonly T* _array;
    private readonly int _byteLength;
    private readonly int _elements;

    private readonly bool _ownsMemory;
    public SimpleNativeArray(ReadOnlySpan<byte> memory, bool zeroMemory = false, bool ownsMemory = false)
    {
        _ownsMemory = ownsMemory;
        fixed (byte* ptr = memory)
        {
            _array = (T*)ptr;
            _byteLength = memory.Length;
        }
        _elements = (_byteLength / Unsafe.SizeOf<T>());
        InitializeArray(zeroMemory);
    }

    public SimpleNativeArray(byte* ptr, int elements, bool zeroMemory = false, bool ownsMemory = false)
    {
        _ownsMemory = ownsMemory;
        _array = (T*)ptr;
        _byteLength = (elements * Unsafe.SizeOf<T>());
        _elements = elements;
        InitializeArray(zeroMemory);
    }

    public readonly void Dispose()
    {
        if (_ownsMemory)
        {
            ZeroMemory();
        }
    }

    private readonly void InitializeArray(bool zeroMemory)
    {
        if (zeroMemory)
        {
            ZeroMemory();
        }
    }

    private readonly void ZeroMemory()
    {
        Unsafe.InitBlockUnaligned(_array, 0, (uint)_byteLength);
    }

    public readonly T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _array[index];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _array[index] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly UInt16 GetLower16(int index)
    {
        if (index < 0 || index >= _elements)
            throw new IndexOutOfRangeException();

        if (sizeof(T) != 4)
            throw new InvalidOperationException("GetLower16 requires 4-byte elements");

        uint value = Unsafe.As<T, uint>(ref _array[index]);
        return (ushort)(value & 0xFFFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void SetLower16(int index, UInt16 value)
    {
        if (index < 0 || index >= _elements)
            throw new IndexOutOfRangeException();

        if (sizeof(T) != 4)
            throw new InvalidOperationException("SetLower16 requires 4-byte elements");

        ref uint slot = ref Unsafe.As<T, uint>(ref _array[index]);
        slot = (slot & 0xFFFF0000u) | value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly T GetValue(int index)
    {
        if (index < 0 || index >= _elements)
        {
            throw new IndexOutOfRangeException($"Index was outside the bounds of the array. Index: {index}, Size: {_elements}");
        }
        return _array[index];
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void SetValue(int index, T value)
    {
        if (index < 0 || index >= _elements)
        {
            throw new IndexOutOfRangeException($"Index was outside the bounds of the array. Index: {index}, Size: {_elements}");
        }
        _array[index] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly NativePointer<T> GetValuePointerWrapped(int index)
    {
        return new NativePointer<T>(GetValuePointer(index));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly T* GetValuePointer(int index)
    {
        if (index < 0 || index >= _elements)
        {
            throw new IndexOutOfRangeException($"Index was outside the bounds of the array. Index: {index}, Size: {_elements}");
        }
        return (T*)&_array[index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ref T GetReference(int index)
    {
        return ref Unsafe.AsRef<T>(GetValuePointer(index));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int GetIndexByAddress(NativePointer<T> element)
    {
        UInt64 elementRVA = (UInt64)element.Pointer - (UInt64)_array;
        return (int)(elementRVA / (UInt64)sizeof(T));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int GetIndexByAddress(UInt64 element)
    {
        UInt64 elementRVA = (UInt64)element - (UInt64)_array;
        return (int)(elementRVA / (UInt64)sizeof(T));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int GetIndexByOffset(UInt64 offset)
    {
        UInt64 index = (UInt64)offset / (UInt64)sizeof(T);
        return (int)index;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly UInt64 GetOffsetByAddress(NativePointer<T> element)
    {
        UInt64 elementRVA = (UInt64)element.Pointer - (UInt64)_array;
        return elementRVA;
    }

    public readonly int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _elements;
    }

    public readonly Span<T> AsSpan() => new(_array, _elements);

    public readonly IntPtr GetArrayAddress() => (IntPtr)_array;
}
