namespace RedBird.X86.Assembly;

/// <summary>How far a disassembly run should follow the instruction stream.</summary>
public enum FunctionEndDetection
{
    /// <summary>
    /// Stop at the first <c>INT3</c>. Cheap, and wrong for any function MSVC laid out with an interior <c>int3</c>, which it emits after every call to a <c>noreturn</c> function
    /// (<c>__report_gsfailure</c>, <c>_invalid_parameter_noinfo_noreturn</c>, <c>abort</c>, throw helpers). Kept for compatibility with existing walk configs.
    /// </summary>
    FirstInt3,

    /// <summary>
    /// Stop at a terminator (<c>ret</c>, unconditional <c>jmp</c>, <c>int3</c>) only once no forward branch seen so far targets an address beyond it. This walks past interior
    /// <c>int3</c> padding and past the epilogue of an early-return path, and lands on the real end of the function for ordinary compiler output.
    /// </summary>
    ReachableTerminator,
}
