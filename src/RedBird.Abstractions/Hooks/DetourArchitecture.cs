namespace RedBird.Abstractions.Hooks;

/// <summary>Instruction-set architecture used by a native detour and its intermediary stubs.</summary>
public enum DetourArchitecture
{
    /// <summary>32-bit x86 (IA-32).</summary>
    X86,

    /// <summary>64-bit x86 (AMD64/x86-64).</summary>
    X64,
}
