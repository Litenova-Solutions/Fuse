using Fuse.Dotnet;

namespace Fuse.Operations;

/// <summary>
///     Groups test failures that share their message's first line, so the answer prints the shared line once as a
///     header. Member lines keep their shape, so the evals keep reading every <c>FAILED</c> line and the
///     <c>also failed</c> names.
/// </summary>
internal static class FailureGroups
{
    /// <summary>The most characters of a first line a header shows; members keep the full text.</summary>
    internal const int MaxHeaderFirstLine = 160;

    /// <summary>
    ///     The failures to print in detail as (header, failure) pairs, in print order, with the header set before a
    ///     group's first member, and the rest as distinct names in run order with how many failures they hold.
    /// </summary>
    /// <param name="failures">Every failure, in run order.</param>
    /// <param name="maxShown">How many failures print in detail.</param>
    internal static (IReadOnlyList<(string? Header, TestFailure Failure)> Detailed, IReadOnlyList<string> RestNames, int RestCount) Split(
        IReadOnlyList<TestFailure> failures, int maxShown)
    {
        var keys = failures.Select(f => FirstLine(f.Message)).ToList();
        var members = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < failures.Count; i++)
        {
            if (keys[i].Length == 0)
                continue;
            if (!members.TryGetValue(keys[i], out var group))
                members[keys[i]] = group = [];
            group.Add(i);
        }

        bool Grouped(int index) => keys[index].Length > 0 && members[keys[index]].Count >= 2;
        string? Head(HashSet<string> headed, int index) =>
            Grouped(index) && headed.Add(keys[index]) ? Header(members[keys[index]].Count, keys[index]) : null;

        if (failures.Count <= maxShown)
        {
            var detailed = new List<(string? Header, TestFailure Failure)>(failures.Count);
            var headed = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < failures.Count; i++)
                detailed.Add((Head(headed, i), failures[i]));
            return (detailed, [], 0);
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

        for (var i = 0; i < failures.Count && indices.Count < maxShown; i++)
        {
            if (!Grouped(i) && shown.Add(i))
                indices.Add(i);
        }

        var detailedOver = new List<(string? Header, TestFailure Failure)>(indices.Count);
        var headedOver = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in indices)
            detailedOver.Add((Head(headedOver, index), failures[index]));

        var rest = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < failures.Count; i++)
        {
            if (!shown.Contains(i) && seen.Add(failures[i].Name))
                rest.Add(failures[i].Name);
        }

        return (detailedOver, rest, failures.Count - shown.Count);
    }

    /// <summary>The first line of <paramref name="message"/>, trimmed; empty when there is none to group by.</summary>
    internal static string FirstLine(string message)
    {
        var end = message.IndexOf('\n');
        return (end < 0 ? message : message[..end]).Trim();
    }

    private static string Header(int count, string firstLine) =>
        $"{count} failure(s) with the same first message line: {(firstLine.Length > MaxHeaderFirstLine ? firstLine[..MaxHeaderFirstLine] + "..." : firstLine)}";
}
