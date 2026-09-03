namespace RedBird.Backends.NativeX86;

/// <summary>Policy for adding an x86 detour to an entry point that is already detoured.</summary>
public enum X86HookChainingPolicy
{
    /// <summary>Install on top of the existing hook.</summary>
    Allow,

    /// <summary>Install on top and emit a warning.</summary>
    WarnAndAllow,

    /// <summary>Refuse to install another layer.</summary>
    Refuse,
}
