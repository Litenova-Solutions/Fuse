using Fuse.Protocol;

namespace Fuse.Operations;

/// <summary>
///     Groups introduced errors that share their cause, so the answer prints the cause once as a header. Member lines
///     keep their shape and column, so the evals keep reading every error line.
/// </summary>
internal static class CheckGroups
{
    /// <summary>
    ///     The errors to print as (header, error, showCause) triples, in print order, with the header set before a
    ///     group's first member and the cause hidden for grouped members.
    /// </summary>
    /// <param name="errors">Every error the engine sent, in report order.</param>
    /// <param name="maxShown">How many errors print.</param>
    internal static IReadOnlyList<(string? Header, ReportedError Error, bool ShowCause)> Split(IReadOnlyList<ReportedError> errors, int maxShown)
    {
        var keys = errors.Select(KeyOf).ToList();
        var members = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < errors.Count; i++)
        {
            if (keys[i] is not { } key)
                continue;
            if (!members.TryGetValue(key, out var group))
                members[key] = group = [];
            group.Add(i);
        }

        bool Grouped(int index) => keys[index] is { } key && members[key].Count >= 2;

        if (errors.Count <= maxShown)
        {
            var fitted = new List<(string? Header, ReportedError Error, bool ShowCause)>(errors.Count);
            var headed = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < errors.Count; i++)
            {
                string? header = null;
                if (keys[i] is { } key && members[key].Count >= 2 && headed.Add(key))
                    header = $"{key} ({members[key].Count} error(s))";
                fitted.Add((header, errors[i], !Grouped(i)));
            }

            return fitted;
        }

        var order = members.Values
            .Where(g => g.Count >= 2)
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g[0])
            .ToList();
        var shown = new HashSet<int>();
        var indices = new List<int>();
        foreach (var group in order)
        {
            foreach (var index in group.Take(2))
            {
                if (indices.Count >= maxShown)
                    break;
                indices.Add(index);
                shown.Add(index);
            }
        }

        for (var i = 0; i < errors.Count && indices.Count < maxShown; i++)
        {
            if (!Grouped(i) && shown.Add(i))
                indices.Add(i);
        }

        var detailed = new List<(string? Header, ReportedError Error, bool ShowCause)>(indices.Count);
        var headedOver = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in indices)
        {
            string? header = null;
            if (keys[index] is { } key && members[key].Count >= 2 && headedOver.Add(key))
                header = $"{key} ({members[key].Count} error(s), first {members[key].Count(shown.Contains)} shown)";
            detailed.Add((header, errors[index], !Grouped(index)));
        }

        return detailed;
    }

    /// <summary>The cause as the text answer prints it under an error, or null for an error with none.</summary>
    private static string? KeyOf(ReportedError error) =>
        error.Cause is null ? null : $"{(error.Cause.Kind == CauseKind.Removed ? "removed" : "changed")}: {error.Cause.Declaration}";
}
