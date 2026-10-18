using System.Text;
using Fuse.Dotnet;
using Fuse.Paths;
using Fuse.Protocol;

namespace Fuse.Operations;

/// <summary>The full results of the last check and test runs, which the console answers cap.</summary>
internal static class ResultsFile
{
    /// <summary>
    ///     Every introduced error of <paramref name="report"/>, as <c>last-check-results.txt</c> in the state
    ///     directory, for the agent to read whole. Always replaces the previous file, even when the check found
    ///     nothing. Null when the file could not be written.
    /// </summary>
    internal static string? WriteCheck(RepoRoot root, CheckReport report)
    {
        var text = new StringBuilder();
        text.Append($"fuse check: {report.ErrorCount} error(s) introduced").Append('\n');
        var generatedShown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reported in report.Errors)
            CheckOperation.AppendError(text, reported, generatedShown);
        // Past what the engine sends, only the count is known.
        if (report.ErrorCount > report.Errors.Length)
            text.Append($"... and {report.ErrorCount - report.Errors.Length} more error(s) the engine did not send").Append('\n');
        return Write(root, "last-check-results.txt", text.ToString());
    }

    /// <summary>
    ///     Every failure of <paramref name="runs"/>, as <c>last-test-results.txt</c> in the state directory, for the
    ///     agent to read whole. Always replaces the previous file, even when nothing failed. Null when the file could
    ///     not be written.
    /// </summary>
    internal static string? WriteTests(RepoRoot root, IEnumerable<(string Project, TrxResults Outcome)> runs)
    {
        var text = new StringBuilder();
        var (failed, passed) = (0, 0);
        foreach (var (project, outcome) in runs)
        {
            failed += outcome.Failed;
            passed += outcome.Passed;
            if (outcome.Failures.Count == 0)
                continue;
            if (project.Length > 0)
                text.Append($"## {project}").Append('\n');
            foreach (var failure in outcome.Failures)
                TestOperation.AppendFailure(text, failure);
        }
        text.Append($"fuse: {failed} failed, {passed} passed").Append('\n');
        return Write(root, "last-test-results.txt", text.ToString());
    }

    /// <summary>Replaces <paramref name="name"/> in the state directory with <paramref name="text"/>; null when it cannot be written.</summary>
    private static string? Write(RepoRoot root, string name, string text)
    {
        try
        {
            Directory.CreateDirectory(root.StateDirectory);
            var path = Path.Combine(root.StateDirectory, name);
            File.WriteAllText(path, text);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
