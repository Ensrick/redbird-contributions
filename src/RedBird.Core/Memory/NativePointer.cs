using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RedBird.Core.Memory;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativePointer<T> where T : unmanaged
{
    public T* Pointer;

    public NativePointer(T* pointer)
    {
        Pointer = pointer;
    }

    public NativePointer(IntPtr pointer)
    {
        Pointer = (T*)pointer;
    }

    public NativePointer(ref T reference)
    {
        Pointer = (T*)Unsafe.AsPointer<T>(ref reference);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ref T AsRef()
    {
        return ref Unsafe.AsRef<T>(Pointer);
    }

    public readonly bool IsNull => Pointer == null;

    // Implicit conversion from T* to NativePointer<T>
    public static implicit operator NativePointer<T>(T* ptr)
    {
        return new NativePointer<T>(ptr);
    }

    // Implicit conversion from NativePointer<T> back to T*
    public static implicit operator T*(NativePointer<T> nativePtr)
    {
        return nativePtr.Pointer;
    }

    // Implicit conversion from IntPtr to NativePointer<T>
    public static implicit operator NativePointer<T>(IntPtr nativePtr)
    {
        return new NativePointer<T>((T*)nativePtr);
    }

    public static bool operator ==(NativePointer<T> left, IntPtr right) => (IntPtr)left.Pointer == right;

    public static bool operator !=(NativePointer<T> left, IntPtr right) => (IntPtr)left.Pointer != right;

    public override readonly bool Equals(object? obj)
    {
        return obj is NativePointer<T> np && np.Pointer == Pointer;
    }

    public override readonly int GetHashCode()
    {
        return base.GetHashCode();
    }

    public override readonly string ToString()
    {
        return $"{new IntPtr(Pointer):X16}";
    }
}
