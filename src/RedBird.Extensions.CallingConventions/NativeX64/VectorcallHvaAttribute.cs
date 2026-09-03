using System;

namespace RedBird.Extensions.CallingConventions.NativeX64;

/// <summary>
/// Classifies a blittable value type as a homogeneous vector aggregate (HVA) for
/// <c>__vectorcall</c>.
/// </summary>
/// <remarks>
/// An HVA contains one through four consecutive, identical members. A member may be a <see cref="Single"/>, <see cref="Double"/>, 128-bit vector, or 256-bit vector.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = false, Inherited = false)]
public sealed class VectorcallHvaAttribute : Attribute
{
    /// <summary>Creates an HVA classification.</summary>
    /// <param name="elementSize">The size of each homogeneous member: 4, 8, 16, or 32 bytes.</param>
    /// <param name="elementCount">The number of homogeneous members, from one through four.</param>
    public VectorcallHvaAttribute(int elementSize, int elementCount)
    {
        if (elementSize is not (4 or 8 or 16 or 32))
        {
            throw new ArgumentOutOfRangeException(
                nameof(elementSize),
                "An HVA member must be a float, double, 128-bit vector, or 256-bit vector.");
        }
        if (elementCount is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(elementCount), "An HVA must contain one through four members.");

        ElementSize = elementSize;
        ElementCount = elementCount;
    }

    /// <summary>Size of each homogeneous member in bytes.</summary>
    public int ElementSize { get; }

    /// <summary>Number of homogeneous members.</summary>
    public int ElementCount { get; }
}
