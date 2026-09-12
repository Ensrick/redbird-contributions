namespace RedBird.X86.Assembly.Switches;

/// <summary>How each 32-bit x86 jump-table entry becomes a virtual address.</summary>
public enum SwitchTableEntryEncoding
{
    /// <summary>The entry is zero-extended and added to a decoded base.</summary>
    UnsignedRelative32 = 0,

    /// <summary>The entry is a 32-bit displacement added to a statically resolved base.</summary>
    SignedRelative32 = 1,

    /// <summary>The entry is an absolute 32-bit code pointer.</summary>
    AbsolutePointer32 = 2,
}
