namespace RedBird.X64.Assembly.Switches;

/// <summary>How selector values address the jump table.</summary>
public enum SwitchTableLayout
{
    /// <summary>The normalized selector directly indexes the jump table.</summary>
    Direct = 0,

    /// <summary>A byte table first maps the normalized selector to a dense jump-table slot.</summary>
    ByteMapped = 1,
}
