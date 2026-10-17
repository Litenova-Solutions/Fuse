using Fuse.Check.Model;

namespace Fuse.Check;

/// <summary>Finds working-tree errors that have no match in the baseline.</summary>
internal static class ErrorDelta
{
    /// <summary>
    ///     Returns the errors in <paramref name="current"/> without a match in <paramref name="baseline"/>. A match is the
    ///     same file, id and message; line numbers are ignored because edits above an error move it. Matching is by
    ///     count, so a second copy of an existing error still counts as introduced.
    /// </summary>
    public static IEnumerable<CompilerError> Introduced(IEnumerable<CompilerError> current, IEnumerable<CompilerError> baseline)
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

    /// <summary>
    ///     Same as <see cref="Introduced(IEnumerable{CompilerError}, IEnumerable{CompilerError})"/>, for a baseline whose
    ///     analyzers may not have finished. When they did not, an analyzer error in <paramref name="current"/> may be one
    ///     HEAD has too, so the analyzer errors are left out and only the compiler errors are compared.
    /// </summary>
    /// <returns>The introduced errors, and whether analyzer errors were left out.</returns>
    public static (List<CompilerError> Errors, bool AnalyzerErrorsLeftOut) Introduced(IEnumerable<CompilerError> current, IEnumerable<CompilerError> baseline, bool baselineAnalyzersFinished)
    {
        var introduced = Introduced(current, baseline).ToList();
        if (baselineAnalyzersFinished)
            return (introduced, false);
        var compiler = introduced.Where(e => !e.FromAnalyzer).ToList();
        return (compiler, compiler.Count < introduced.Count);
    }
}
