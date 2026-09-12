namespace RedBird.X64.Assembly.Switches;

/// <summary>How each 32-bit jump-table entry becomes a virtual address.</summary>
public enum SwitchTableEntryEncoding
{
    /// <summary>The entry is zero-extended and added to the decoded table base.</summary>
    UnsignedRelative32 = 0,

    /// <summary>The entry is sign-extended and added to the decoded table base.</summary>
    SignedRelative32 = 1,
}
