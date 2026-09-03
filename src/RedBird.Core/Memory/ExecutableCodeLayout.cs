using System;

namespace RedBird.Core.Memory;

/// <summary>Addresses handed to a builder so it can emit RIP-relative references.</summary>
/// <param name="codeAddress">Where the first instruction will live.</param>
/// <param name="dataAddress">Where the scratch slot lives.</param>
public readonly struct ExecutableCodeLayout(UInt64 codeAddress, UInt64 dataAddress)
{
    /// <summary>Address the first instruction is assembled at.</summary>
    public UInt64 CodeAddress { get; } = codeAddress;

    /// <summary>Address of the 32-bit scratch slot.</summary>
    public UInt64 DataAddress { get; } = dataAddress;

    /// <summary>Writes an int into the scratch slot.</summary>
    public unsafe void SetData(int value) => *(int*)DataAddress = value;
}
