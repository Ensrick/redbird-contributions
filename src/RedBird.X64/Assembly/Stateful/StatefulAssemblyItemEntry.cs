namespace RedBird.X64.Assembly.Stateful;

/// <summary>A registered site plus the metadata the host needs to decide who may touch it.</summary>
public sealed class StatefulAssemblyItemEntry(string name, IAssemblyValueSite site, bool exposed = true)
{
    /// <summary>Name the site is registered and scripted under.</summary>
    public string Name { get; } = name;

    /// <summary>The site itself.</summary>
    public IAssemblyValueSite Site { get; } = site;

    /// <summary>Whether external scripting may read and change it.</summary>
    public bool Exposed { get; } = exposed;

    /// <inheritdoc/>
    public override string ToString() => $"{Name}: {Site}{(Exposed ? string.Empty : " (internal)")}";
}
