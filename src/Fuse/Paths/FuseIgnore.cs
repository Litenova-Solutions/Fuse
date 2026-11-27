namespace Fuse.Paths;

/// <summary>
///     The optional repository ignore file: <c>.fuseignore</c> in the repository root with path patterns, one per
///     line. A pattern is a <see cref="PathPattern"/>: <c>**</c> as a whole segment matches zero or more segments and
///     <c>*</c> and <c>?</c> match inside one segment, and a pattern with no wildcards names a file or a directory
///     subtree. A line that is blank and a line that starts with <c>#</c> is a comment. No other syntax and no
///     behavior flags exist. Fuse never writes the file.
/// </summary>
internal sealed class FuseIgnore
{
    private readonly List<string> _patterns;

    private FuseIgnore(List<string> patterns)
    {
        _patterns = patterns;
    }

    /// <summary>Reads the ignore file in <paramref name="root"/>. A missing or unreadable file ignores nothing.</summary>
    public static FuseIgnore Read(RepoRoot root)
    {
        var patterns = new List<string>();
        string[] lines;
        try
        {
            var file = Path.Combine(root.Path, ".fuseignore");
            if (!File.Exists(file))
                return new FuseIgnore(patterns);
            lines = File.ReadAllLines(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new FuseIgnore(patterns);
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (PathPattern.Normalize(line, out var normalized))
                patterns.Add(normalized);
        }

        return new FuseIgnore(patterns);
    }

    /// <summary>True when <paramref name="path"/> matches a pattern in the ignore file.</summary>
    public bool Ignores(RepoPath path)
    {
        var relative = path.Relative;
        foreach (var pattern in _patterns)
        {
            if (PathPattern.Matches(pattern, relative))
                return true;
        }

        return false;
    }
}
