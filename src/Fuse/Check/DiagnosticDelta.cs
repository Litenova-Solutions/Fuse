using Fuse.Protocol;

namespace Fuse.Check;

/// <summary>Finds working-tree errors that have no match in the baseline.</summary>
internal static class DiagnosticDelta
{
    /// <summary>
    ///     Returns the diagnostics in <paramref name="current"/> without a match in <paramref name="baseline"/>. A match
    ///     is the same file, id and message; line numbers are ignored because edits above an error move it. Matching is
    ///     by count, so a second copy of an existing error still counts as introduced.
    /// </summary>
    public static IEnumerable<Diagnostic> Introduced(IEnumerable<Diagnostic> current, IEnumerable<Diagnostic> baseline)
    {
        var remaining = new Dictionary<(string, string, string), int>();
        foreach (var d in baseline)
        {
            var key = (d.Path, d.Id, d.Message);
            remaining[key] = remaining.GetValueOrDefault(key) + 1;
        }

        foreach (var d in current)
        {
            var key = (d.Path, d.Id, d.Message);
            if (remaining.TryGetValue(key, out var count) && count > 0)
            {
                remaining[key] = count - 1;
                continue;
            }

            yield return d;
        }
    }
}
