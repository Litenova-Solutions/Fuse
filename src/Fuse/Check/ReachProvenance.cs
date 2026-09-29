using Fuse.Paths;


namespace Fuse.Check;

/// <summary>
///     Which changed declaration made each candidate file a candidate. A candidate is a file fuse did not ask about but had
///     to bind, because something the agent changed is used in it; this is what says which thing, so an error there can
///     name its cause instead of leaving the agent to work backwards from the file name.
/// </summary>
internal sealed class ReachProvenance
{
    private readonly Dictionary<string, List<(string Declaration, bool Removed)>> _byFile = new(PathRules.PathComparer);

    /// <summary>Every candidate file, the union over all changed declarations.</summary>
    public HashSet<string> Files { get; } = new(PathRules.PathComparer);

    /// <summary>Records that <paramref name="declaration"/> is why <paramref name="file"/> is a candidate.</summary>
    public void Add(string file, string declaration, bool removed)
    {
        Files.Add(file);
        if (!_byFile.TryGetValue(file, out var declarations))
            _byFile[file] = declarations = [];
        declarations.Add((declaration, removed));
    }

    /// <summary>Records every file in <paramref name="files"/> as a candidate for the same reason.</summary>
    public void AddRange(IEnumerable<string> files, string declaration, bool removed)
    {
        foreach (var file in files)
            Add(file, declaration, removed);
    }

    /// <summary>
    ///     The first changed declaration that made <paramref name="file"/> a candidate, with whether it was removed at
    ///     HEAD, or null when nothing explains the file. Candidates are added in the order the changes were seen, so this
    ///     is the first cause, not the nearest one.
    /// </summary>
    public (string Declaration, bool Removed)? For(string file) =>
        _byFile.TryGetValue(file, out var declarations) && declarations.Count > 0 ? declarations[0] : null;
}
