using Fuse.Failures;
using Fuse.Paths;
using Fuse.Workspace;

namespace Fuse.Check;

/// <summary>
///     Turns the paths a scoped check names into its targets: the changed sources under its directories and patterns, and
///     the files it names. A spec that matches no C# source the check could cover is refused instead
///     of answering clean, which would read as no errors introduced.
/// </summary>
internal static class ScopeExpansion
{
    /// <summary>The most sources one scoped check covers; past it the check is refused.</summary>
    public const int MaxExpandedFiles = 2000;

    /// <summary>
    ///     The targets of the specs <paramref name="named"/>, after the sync: the union of each directory's changed files,
    ///     each glob's matches and each file, kept to C# sources a project owns, de-duplicated, in path order.
    /// </summary>
    /// <param name="workspace">The repository, whose change tracker is the universe a directory or a glob matches in.</param>
    /// <param name="named">Absolute file and directory paths and absolute glob patterns, in the order named.</param>
    /// <exception cref="FuseException">A directory, a glob or a file matches nothing checkable, or the expansion is too large.</exception>
    public static List<RepoPath> Expand(RepoWorkspace workspace, IReadOnlyList<string> named)
    {
        var root = workspace.Root;
        var graph = workspace.Graph;
        var changed = workspace.Tracker.Changed;
        var union = new HashSet<RepoPath>();
        foreach (var spec in named)
        {
            if (spec.Contains('*') || spec.Contains('?'))
            {
                var display = DisplaySpec(root, spec);
                if (!PathPattern.Normalize(ToRepoRelative(root, spec, display), out var pattern))
                    throw new FuseException(ErrorCode.InvalidPath, ErrorMessages.ScopeMatchesNothing(display));
                var matched = changed.Where(p => PathPattern.Matches(pattern, p.Relative)).ToList();
                var kept = matched.Where(p => PathRules.IsSource(p.Absolute)).Where(p => graph.OwnersOf(p).Count > 0).ToList();
                if (kept.Count == 0)
                    throw new FuseException(ErrorCode.InvalidPath, ErrorMessages.ScopeMatchesNothing(display));
                union.UnionWith(kept);
                continue;
            }

            var absolute = Absolute(root, spec);
            if (Directory.Exists(absolute))
            {
                var display = DisplaySpec(root, spec);
                var directory = root.PathOfNamed(absolute);
                var kept = changed.Where(p => p.IsUnder(directory))
                    .Where(p => PathRules.IsSource(p.Absolute))
                    .Where(p => graph.OwnersOf(p).Count > 0)
                    .ToList();
                if (kept.Count == 0)
                    throw new FuseException(ErrorCode.InvalidPath, ErrorMessages.ScopeMatchesNothing(display));
                union.UnionWith(kept);
                continue;
            }

            RepoPath file;
            try
            {
                file = root.PathOfNamed(spec);
            }
            catch (Exception e) when (e is ArgumentException or PathTooLongException)
            {
                throw new FuseException(ErrorCode.InvalidPath, ErrorMessages.InvalidPath(spec));
            }

            if (!PathRules.IsSource(file.Absolute) || graph.OwnersOf(file).Count == 0)
                throw new FuseException(ErrorCode.InvalidPath, ErrorMessages.ScopeMatchesNothing(DisplaySpec(root, spec)));
            union.Add(file);
        }

        var targets = union.Where(p => PathRules.IsSource(p.Absolute))
            .Where(p => graph.OwnersOf(p).Count > 0)
            .Distinct()
            .OrderBy(p => p.Relative, StringComparer.Ordinal)
            .ToList();
        if (targets.Count > MaxExpandedFiles)
            throw new FuseException(ErrorCode.InvalidPath, ErrorMessages.ScopeTooManyFiles(targets.Count));
        return targets;
    }

    /// <summary>
    ///     The sources under <paramref name="named"/> written in the last <see cref="RecentWindow"/>, and every named file,
    ///     for the sync to read from disk before the file watcher reports them, as a files scope does with its files. A
    ///     check that runs right after an edit then sees it. Older files the watcher has reported already; a directory or
    ///     pattern stops being searched past <see cref="MaxSearchedFiles"/> files.
    /// </summary>
    public static List<RepoPath> RecentlyWritten(RepoRoot root, IReadOnlyList<string> named)
    {
        var since = DateTime.UtcNow - RecentWindow;
        var result = new List<RepoPath>();
        foreach (var spec in named)
        {
            try
            {
                if (spec.Contains('*') || spec.Contains('?'))
                {
                    var display = DisplaySpec(root, spec);
                    if (!PathPattern.Normalize(ToRepoRelative(root, spec, display), out var pattern))
                        continue;
                    var literal = pattern[..Math.Max(0, pattern[..pattern.IndexOfAny(['*', '?'])].LastIndexOf('/'))];
                    var start = literal.Length == 0 ? root.Path : Path.Combine(root.Path, literal);
                    result.AddRange(Recent(root, start, since).Where(p => PathPattern.Matches(pattern, p.Relative)));
                }
                else if (Directory.Exists(Absolute(root, spec)))
                {
                    result.AddRange(Recent(root, Absolute(root, spec), since));
                }
                else
                {
                    result.Add(root.PathOfNamed(spec));
                }
            }
            catch (Exception e) when (e is FuseException or ArgumentException or PathTooLongException or IOException or UnauthorizedAccessException)
            {
                // A spec that cannot be read here is refused, or answered from the watcher, by the expansion after the sync.
            }
        }

        return result;
    }

    /// <summary>How far back <see cref="RecentlyWritten"/> looks: far longer than the watcher takes to report a write.</summary>
    private static readonly TimeSpan RecentWindow = TimeSpan.FromSeconds(10);

    /// <summary>The most files <see cref="RecentlyWritten"/> looks at under one directory or pattern.</summary>
    private const int MaxSearchedFiles = 20000;

    private static List<RepoPath> Recent(RepoRoot root, string directory, DateTime since)
    {
        if (!Directory.Exists(directory))
            return [];
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        return new DirectoryInfo(directory).EnumerateFiles("*", options)
            .Take(MaxSearchedFiles)
            .Where(f => PathRules.IsSource(f.FullName) && f.LastWriteTimeUtc >= since)
            .Select(f => root.PathOf(f.FullName))
            .ToList();
    }

    /// <summary>The specs as the summary names them: repository-relative with forward slashes, in the order named.</summary>
    public static IReadOnlyList<string> DisplaySpecs(RepoRoot root, IReadOnlyList<string> named) =>
        [.. named.Select(s => DisplaySpec(root, s))];

    private static string DisplaySpec(RepoRoot root, string spec)
    {
        try
        {
            return root.PathOfNamed(ToAbsolute(root, spec)).Relative;
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException or IOException)
        {
            return spec.Replace('\\', '/');
        }
    }

    /// <summary><paramref name="spec"/> as an absolute path, reading a relative one from the root.</summary>
    private static string Absolute(RepoRoot root, string spec)
    {
        try
        {
            return Path.GetFullPath(spec, root.Path);
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            throw new FuseException(ErrorCode.InvalidPath, ErrorMessages.InvalidPath(spec));
        }
    }

    private static string ToAbsolute(RepoRoot root, string spec)
    {
        try
        {
            return Path.IsPathRooted(spec) ? spec : Path.Combine(root.Path, spec);
        }
        catch (Exception e) when (e is ArgumentException)
        {
            throw new FuseException(ErrorCode.InvalidPath, ErrorMessages.InvalidPath(spec));
        }
    }

    /// <summary><paramref name="spec"/>, an absolute or relative glob, as a repository-relative pattern.</summary>
    private static string ToRepoRelative(RepoRoot root, string spec, string display)
    {
        if (!Path.IsPathRooted(spec))
            return spec.Replace('\\', '/');
        return PathRules.UnderRoot(root.Path, spec) ?? display;
    }
}
