namespace RedBird.Backends.NativeX86;

/// <summary>Entry-patch encoding used by an x86 detour.</summary>
public enum X86DetourScheme
{
    /// <summary><c>jmp rel32</c>, five bytes, position independent and able to reach all of x86.</summary>
    Direct,
}
