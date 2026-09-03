using System;

namespace RedBird.Backends.NativeX64;

/// <summary>The bytes to write at a hook site, plus whatever had to be allocated for them.</summary>
internal sealed class DetourPatch
{
    /// <summary>The scheme that produced these bytes.</summary>
    public required DetourScheme Scheme { get; init; }

    /// <summary>Bytes to write at the function entry, before NOP padding.</summary>
    public required byte[] Bytes { get; init; }

    /// <summary>Pointer slot allocated for <see cref="DetourScheme.Indirect"/>, or zero.</summary>
    public IntPtr PointerSlot { get; init; }
}
