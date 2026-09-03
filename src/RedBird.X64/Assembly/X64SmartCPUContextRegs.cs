using System;

namespace RedBird.X64.Assembly;

/// <summary>
/// Represents the registers in a 64-bit CPU context that can be manipulated by the X64SmartCPUContext class.
/// </summary>
[Flags]
public enum X64SmartCPUContextRegs
{
    None = 0,
    RAX = 1 << 0,
    RBX = 1 << 1,
    RCX = 1 << 2,
    RDX = 1 << 3,
    RSI = 1 << 4,
    RDI = 1 << 5,
    RBP = 1 << 6,
    RSP = 1 << 7,
    R8 = 1 << 8,
    R9 = 1 << 9,
    R10 = 1 << 10,
    R11 = 1 << 11,
    R12 = 1 << 12,
    R13 = 1 << 13,
    R14 = 1 << 14,
    R15 = 1 << 15,
    Flags = 1 << 16,

    // Shortcuts
    Arguments = RCX | RDX | R8 | R9,
    Volatile = RAX | RCX | RDX | R8 | R9 | R10 | R11,
    All = 0x1FFFF
}
