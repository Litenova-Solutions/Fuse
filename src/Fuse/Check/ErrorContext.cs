using Fuse.Protocol;
using Fuse.Repo;

namespace Fuse.Check;

/// <summary>
///     The one line that says why an error in a file the agent did not touch is being reported. Without it an agent
///     reading <c>src/App/Program.cs(12,5): error CS1061</c> has to guess which of its own edits caused it; with it the
///     answer is on the next line. A target file is not given one: the agent just edited it and knows why.
/// </summary>
internal static class ErrorContext
{
    /// <summary>At most this many context lines per response, so a wide change cannot bury the errors it explains.</summary>
    public const int MaxLines = 10;

    /// <summary>
    ///     The context line for each diagnostic, in the same order, with null where a diagnostic gets none: a target file,
    ///     an analyzer diagnostic, or a line the cap left out. The cap is counted over the lines that would have been
    ///     printed, so the count returned is what the caller says it left out.
    /// </summary>
    /// <param name="diagnostics">The errors in the check's answer, in report order.</param>
    /// <param name="provenance">Which changed declaration made each candidate file a candidate, or null when the change was broad.</param>
    /// <param name="absolute">Turns a diagnostic's repository-relative path into the absolute one the provenance is keyed by.</param>
    /// <param name="targets">Absolute paths of the files the agent edited, which never get a line.</param>
    public static (string?[] Context, int LeftOut) Build(
        IReadOnlyList<Diagnostic> diagnostics,
        ReachProvenance? provenance,
        Func<string, string> absolute,
        IReadOnlyCollection<string> targets)
    {
        var context = new string?[diagnostics.Count];
        if (provenance is null)
            return (context, 0);

        var targetPaths = new HashSet<string>(targets, ChangeTracker.PathComparer);
        var shown = 0;
        var leftOut = 0;
        foreach (var (diagnostic, index) in diagnostics.Select((d, i) => (d, i)))
        {
            var path = absolute(diagnostic.Path);
            if (diagnostic.Analyzer || targetPaths.Contains(path) || provenance.For(path) is not { } cause)
                continue;

            if (shown >= MaxLines)
            {
                leftOut++;
                continue;
            }

            context[index] = $"{(cause.Removed ? "removed" : "changed")}: {cause.Declaration}";
            shown++;
        }

        return (context, leftOut);
    }
}
