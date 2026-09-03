using System;
using System.Runtime.CompilerServices;

namespace RedBird.Core.Memory.Managed;

/// <summary>
/// Represents a 2D Matrix with Stack support.
/// Wraps ManagedNativeArray logic but handles 2D indexing.
/// </summary>
public unsafe class ManagedNativeMatrix<T>(void* baseAddress, int totalItems, int width) : IDisposable where T : unmanaged
{
    private readonly ManagedNativeArray<T> _backingArray = new(baseAddress, totalItems);
    private readonly int _width = width; // Stride

    public ManagedNativeMatrix(IntPtr baseAddress, int totalItems, int width) : this((void*)baseAddress, totalItems, width)
    {

    }

    public ManagedNativeArray<T> GetBackingArray() => _backingArray;

    /// <summary>
    /// Reverts to Base values (removes Map overrides).
    /// </summary>
    public void ClearOverrides() => _backingArray.ClearOverrides();

    /// <summary>
    /// Reverts to Vanilla values (removes Mod defaults).
    /// </summary>
    public void ResetToOriginal() => _backingArray.ResetToOriginal();


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetIndex(int x, int y) => x * _width + y;

    public T GetValue(int x, int y) => _backingArray.GetValue(GetIndex(x, y));

    public void SetValue(int x, int y, T value) => _backingArray.SetValue(GetIndex(x, y), value);

    public void Push(int x, int y, T value) => _backingArray.Push(GetIndex(x, y), value);

    public T Pop(int x, int y) => _backingArray.Pop(GetIndex(x, y));

    public void SetBase(int x, int y, T value) => _backingArray.SetBase(GetIndex(x, y), value);

    public void Dispose()
    {
        _backingArray.Dispose();
        GC.SuppressFinalize(this);
    }
}
