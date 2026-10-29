using System.Text;
using Fuse.Dotnet;
using Fuse.Paths;
using Fuse.Protocol;

namespace Fuse.Operations;

/// <summary>
///     Runs the tests that failed last time before the rest: which recorded failures are among a plan's selected tests,
///     and the two VSTest filters a split project runs with. The client builds both filters itself, since the engine
///     answered the plan already; the spelling matches <c>Fuse.Testing.TestFilter</c>, which this namespace must not use.
/// </summary>
internal static class FailedFirstSplit
{
    /// <summary>The most patterns a VSTest filter holds, the same bound the engine builds filters with.</summary>
    internal const int MaxPatterns = 2000;

    /// <summary>The most recorded failures one project splits on; more run as one stage.</summary>
    internal const int MaxPriority = 100;

    /// <summary>
    ///     Each VSTest project of <paramref name="plan"/> whose selection holds recorded failures as
    ///     (absolute project path, distinct failure names in ordinal order), or null when no project splits. A project
    ///     splits only for a method selection with between 1 and <see cref="MaxPriority"/> recorded failures in it;
    ///     platform projects and whole runs never split, and neither does a project whose split would name a theory row:
    ///     the stripped name cannot exclude the row from the rest, so the project runs as one stage.
    /// </summary>
    internal static IReadOnlyDictionary<string, string[]>? ForPlan(RepoRoot root, TestPlan plan)
    {
        var record = LastTestResults.Read(root);
        if (record is null)
            return null;
        var failed = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var stripped = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (project, _, tests) in record)
        {
            if (project.Length == 0)
                continue;
            var pairs = tests.Where(t => t.Verdict == TestVerdict.Failed).Select(t => (Stripped: StripTheoryArgs(t.Name), t.Name)).ToList();
            var names = pairs.Select(p => p.Stripped).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            if (names.Length > 0)
            {
                failed[project] = names;
                stripped[project] = pairs.Where(p => p.Stripped != p.Name).Select(p => p.Stripped).ToHashSet(StringComparer.Ordinal);
            }
        }

        if (failed.Count == 0)
            return null;
        var split = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var group in plan.Runs.GroupBy(r => r.Project).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (!failed.TryGetValue(group.Key, out var names))
                continue;
            var runs = group.ToList();
            if (runs.Any(r => r.UsesTestingPlatform) || runs.Any(r => r.Filter != runs[0].Filter) || runs[0].Filter is not string filter)
                continue;
            var patterns = ExtractPatterns(filter);
            if (patterns.Count == 0)
                continue;
            var priority = names.Where(n => patterns.Any(p => n.Contains(p, StringComparison.Ordinal))).ToArray();
            if (priority.Length >= 1 && priority.Length <= MaxPriority
                && !(stripped.TryGetValue(group.Key, out var cut) && priority.Any(cut.Contains)))
                split[group.Key] = priority;
        }

        return split.Count > 0 ? split : null;
    }

    /// <summary>
    ///     The two filters a split project runs: <paramref name="priorityFilter"/> holds only
    ///     <paramref name="priorityNames"/>, and <paramref name="restFilter"/> holds the original selection without them.
    ///     False when the exclusion would pass the pattern bound; the caller then runs one stage.
    /// </summary>
    internal static bool TrySplit(string originalFilter, IReadOnlyList<string> priorityNames, out string priorityFilter, out string restFilter)
    {
        priorityFilter = "";
        restFilter = "";
        var patterns = ExtractPatterns(originalFilter);
        if (patterns.Count == 0 || patterns.Count + priorityNames.Count > MaxPatterns)
            return false;
        priorityFilter = Join(priorityNames);
        restFilter = $"({originalFilter})" + string.Concat(priorityNames.OrderBy(n => n, StringComparer.Ordinal).Select(n => "&FullyQualifiedName!=" + Escape(n)));
        return true;
    }

    /// <summary>The patterns a plain <c>FullyQualifiedName~</c> join holds, unescaped; empty when it is not one.</summary>
    internal static IReadOnlyList<string> ExtractPatterns(string filter)
    {
        if (filter.Length == 0)
            return [];
        var parts = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < filter.Length; i++)
        {
            var c = filter[i];
            if (c == '\\' && i + 1 < filter.Length)
            {
                current.Append(filter[i + 1]);
                i++;
            }
            else if (c == '|')
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else if (c is '&' or '(' or ')')
            {
                return [];
            }
            else
            {
                current.Append(c);
            }
        }

        parts.Add(current.ToString());
        const string prefix = "FullyQualifiedName~";
        if (parts.Any(p => !p.StartsWith(prefix, StringComparison.Ordinal)))
            return [];
        // The split above already dropped every escape backslash, so the rest is the pattern as selected.
        return parts.Select(p => p[prefix.Length..]).ToList();
    }

    /// <summary>A recorded test name as a filter matches it: a theory row without its argument values.</summary>
    internal static string StripTheoryArgs(string name)
    {
        var args = name.IndexOf('(');
        return args < 0 ? name : name[..args].Trim();
    }

    private static string Join(IEnumerable<string> patterns) =>
        string.Join("|", patterns.OrderBy(p => p, StringComparer.Ordinal).Select(p => "FullyQualifiedName~" + Escape(p)));

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or '(' or ')' or '&' or '|' or '=' or '!' or '~')
                builder.Append('\\');
            builder.Append(c);
        }

        return builder.ToString();
    }
}
