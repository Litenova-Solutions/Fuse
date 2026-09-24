namespace Fuse.Testing;

/// <summary>The tests to run in one test project: all of them, or those whose fully qualified name contains one of <see cref="Patterns"/>.</summary>
internal sealed class ProjectSelection
{
    public bool All { get; set; }

    /// <summary>Fully qualified method names (<c>Ns.Outer+Inner.Method</c>) or class prefixes (<c>Ns.Class.</c>).</summary>
    public HashSet<string> Patterns { get; } = new(StringComparer.Ordinal);

    /// <summary>Why <see cref="All"/> was set, when it was.</summary>
    public string? AllReason { get; set; }
}
