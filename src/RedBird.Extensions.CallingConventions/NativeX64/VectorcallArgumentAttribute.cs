using System;

namespace RedBird.Extensions.CallingConventions.NativeX64;

/// <summary>
/// Marks a 16-byte or 32-byte blittable value as a native SIMD vector argument. 
/// Scalar <see cref="Single"/> and <see cref="Double"/> arguments are recognized automatically.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = false, Inherited = false)]
public sealed class VectorcallArgumentAttribute : Attribute
{
    /// <summary>Creates a vector classification with a size in bytes.</summary>
    public VectorcallArgumentAttribute(int size)
    {
        if (size != 16 && size != 32)
            throw new ArgumentOutOfRangeException(nameof(size), "A vectorcall SIMD value must be 16 or 32 bytes.");

        Size = size;
    }

    /// <summary>Vector width in bytes.</summary>
    public int Size { get; }
}
