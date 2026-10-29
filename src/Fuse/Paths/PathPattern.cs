namespace Fuse.Paths;

/// <summary>
///     A path pattern against repository-relative paths: segments split on <c>/</c>, where <c>**</c> as a whole segment
///     matches zero or more segments and <c>*</c> and <c>?</c> match inside one segment. A pattern with no wildcards names
///     a file or, when a path lies under it, a directory subtree. How two spellings compare is <see cref="RepoPath"/>'s.
/// </summary>
internal static class PathPattern
{
    /// <summary>The longest pattern a registration or a scoped check accepts.</summary>
    public const int MaxLength = 256;

    /// <summary>
    ///     True when <paramref name="pattern"/> is a pattern: not blank, at most <see cref="MaxLength"/> characters, no
    ///     control characters, no <c>..</c> segment, and <c>**</c> only as a whole segment. <paramref name="normalized"/> is
    ///     then the pattern with forward slashes and no empty segments, so <c>Lib/</c> reads as <c>Lib</c>.
    /// </summary>
    public static bool Normalize(string? pattern, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > MaxLength || pattern.Any(char.IsControl))
            return false;
        if (pattern.Contains("..", StringComparison.Ordinal) && pattern.Replace('\\', '/').Split('/').Any(s => s == ".."))
            return false;
        var segments = pattern.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;
        foreach (var segment in segments)
        {
            if (segment == ".")
                continue;
            if (segment.Contains("**", StringComparison.Ordinal) && segment != "**")
                return false;
        }

        var kept = segments.Where(s => s != ".").ToArray();
        if (kept.Length == 0)
            return false;
        normalized = string.Join("/", kept);
        return true;
    }

    /// <summary>
    ///     True when <paramref name="repoRelativePath"/> is under <paramref name="pattern"/>: equal to it, under it when it
    ///     holds no wildcards, or matched by it. Both use forward slashes, as <see cref="RepoPath.Relative"/> writes them.
    /// </summary>
    public static bool Matches(string pattern, string repoRelativePath)
    {
        var wanted = pattern.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var actual = repoRelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (wanted.Length == 0 || actual.Length == 0)
            return false;
        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            if (actual.Length < wanted.Length)
                return false;
            for (var i = 0; i < wanted.Length; i++)
            {
                if (!string.Equals(wanted[i], actual[i], RepoPath.Comparison))
                    return false;
            }

            return true;
        }

        return MatchesSegments(wanted, 0, actual, 0);
    }

    private static bool MatchesSegments(string[] wanted, int want, string[] actual, int have)
    {
        if (want == wanted.Length)
            return have == actual.Length;
        if (wanted[want] == "**")
        {
            for (var skip = have; skip <= actual.Length; skip++)
            {
                if (MatchesSegments(wanted, want + 1, actual, skip))
                    return true;
            }

            return false;
        }

        return have < actual.Length && MatchesSegment(wanted[want], actual[have]) && MatchesSegments(wanted, want + 1, actual, have + 1);
    }

    /// <summary>True when <paramref name="segment"/> matches <paramref name="name"/>: <c>*</c> holds any run, <c>?</c> one character.</summary>
    private static bool MatchesSegment(string segment, string name)
    {
        var s = 0;
        var n = 0;
        var star = -1;
        var mark = 0;
        while (n < name.Length)
        {
            if (s < segment.Length && (segment[s] == '?' || Equals(segment[s], name[n])))
            {
                s++;
                n++;
            }
            else if (s < segment.Length && segment[s] == '*')
            {
                star = s++;
                mark = n;
            }
            else if (star >= 0)
            {
                s = star + 1;
                n = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (s < segment.Length && segment[s] == '*')
            s++;
        return s == segment.Length;
    }

    private static bool Equals(char a, char b) =>
        RepoPath.Comparison == StringComparison.Ordinal ? a == b : char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}
