using System.Text;
using Fuse.Engine.Client;
using Fuse.Failures;
using Fuse.Paths;
using Fuse.Protocol;

namespace Fuse.Operations;

/// <summary>Asks the engine for the errors introduced since HEAD and renders them.</summary>
internal static class CheckOperation
{
    private const int MaxShown = 20;

    /// <param name="files">Absolute paths of the files to check, or null for every change.</param>
    /// <param name="session">
    ///     The session that just wrote <paramref name="files"/>, or that is stopping when <paramref name="files"/> is null,
    ///     as its harness identifies it, or null. With one, the answer leaves out the errors other sessions' edits caused
    ///     without it.
    /// </param>
    /// <param name="waitForLoad">Wait for the engine to finish loading, or return immediately with <see cref="ErrorCode.Loading"/>.</param>
    /// <param name="compilationWideAnalyzers">
    ///     False to leave out the analyzers that read the whole compilation, which the summary then names. Ignored when
    ///     <paramref name="files"/> is null: a check of every change runs them.
    /// </param>
    /// <param name="timeout">How long to wait for the answer.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public static async Task<(OperationResult Result, EngineResponse Response)> RunAsync(
        RepoRoot root, IReadOnlyList<string>? files, string? session, bool waitForLoad, bool compilationWideAnalyzers, TimeSpan timeout, CancellationToken cancellationToken)
    {
        EngineRequest request = files is null
            ? new EngineRequest.CheckChanges(waitForLoad) { Session = session, IsStopping = session is not null }
            : new EngineRequest.CheckFiles(files, waitForLoad) { Session = session, LeaveOutCompilationWide = !compilationWideAnalyzers };
        var response = await EngineClient.SendAsync(root, request, timeout, cancellationToken).ConfigureAwait(false);
        if (response is not EngineResponse.CheckAnswered answered)
            return (OperationResult.Unanswered(response, "check result"), response);
        return (Render(answered.Report), response);
    }

    /// <summary>
    ///     The absolute paths of the files a command line or an MCP call names, or the refusal to print when one is empty
    ///     or is not a path. The refusal is <see cref="ErrorCode.InvalidPath"/>'s message with <see cref="Outcome.Unanswered"/>,
    ///     which the engine gives a request line naming such a file, so every surface answers the same way.
    /// </summary>
    /// <param name="files">The files as the caller named them; null stands for an entry that is not a string.</param>
    /// <param name="absolute">Resolves one name: against the current directory for the command line, against the root for MCP.</param>
    public static (IReadOnlyList<string> Files, OperationResult? Refusal) ResolveFiles(IEnumerable<string?> files, Func<string, string> absolute)
    {
        var resolved = new List<string>();
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file))
                return ([], new OperationResult(Outcome.Unanswered, $"fuse: {ErrorMessages.EmptyPath}"));
            try
            {
                resolved.Add(absolute(file));
            }
            catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
            {
                return ([], new OperationResult(Outcome.Unanswered, $"fuse: {ErrorMessages.InvalidPath(file)}"));
            }
        }

        return (resolved, null);
    }

    /// <summary>The line under an error about something a file the build generates declares.</summary>
    internal static string GeneratedLine(string file) =>
        $"generated: {file} declares it, and Fuse reads that file as the last build wrote it; if it is generated from changed code, run fuse build to write it again";

    internal static OperationResult Render(CheckReport report)
    {
        var text = new StringBuilder();
        var generatedShown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reported in report.Errors.Take(MaxShown))
        {
            text.Append(reported.Error).Append('\n');
            // The line under an error in a file the agent did not edit names the declaration change that made the file a candidate.
            if (reported.Cause is { } cause)
                text.Append("  ").Append(cause.Kind == CauseKind.Removed ? "removed" : "changed").Append(": ").Append(cause.Declaration).Append('\n');
            // Said once per file: the file may be older than the code it is generated from.
            if (reported.GeneratedFile is { } generated && generatedShown.Add(generated))
                text.Append("  ").Append(GeneratedLine(generated)).Append('\n');
        }

        var summaryParts = new List<string>();
        if (report.DeclarationsChangedIn.Length > 0)
            summaryParts.Add($"{string.Join(", ", report.DeclarationsChangedIn)} declarations changed, {report.DependentProjectsChecked} dependent project(s) checked");
        if (report.CheckedWholeProjects)
            summaryParts.Add("checked whole projects");
        // Counted among the printed errors only, since a cause left out of an error that is not printed is no loss.
        var causesLeftOut = report.Errors.Take(MaxShown).Count(e => e.IsCauseLeftOut);
        if (causesLeftOut > 0)
            summaryParts.Add($"{causesLeftOut} cause(s) left out");
        // Only an answer to a session leaves errors to other sessions; the session is told they exist, not what they are.
        if (report.LeftToOtherSessions > 0)
            summaryParts.Add($"{report.LeftToOtherSessions} error(s) from other sessions' edits left out");
        // Past the files attribution decides, errors are told as a check without a session tells them.
        if (report.FilesNotAttributed > 0)
            summaryParts.Add($"errors in {report.FilesNotAttributed} file(s) not compared with other sessions' edits, so some of them may be other sessions'");
        if (report.CompilationWideLeftOut.Length > 0)
            summaryParts.Add($"left out {report.CompilationWideLeftOut.Length} analyzer(s) that read the whole project, which the stop hook and fuse check run: {string.Join(", ", report.CompilationWideLeftOut)}");
        if (report.AnalyzersUnfinished > 0)
            summaryParts.Add($"analyzers did not finish in {report.AnalyzersUnfinished} file(s) or project(s) in time, so their analyzer errors may be missing; fuse build runs every analyzer");
        var summaryTail = summaryParts.Count > 0 ? "; " + string.Join("; ", summaryParts) : "";

        if (report.ErrorCount == 0)
        {
            var whose = report.LeftToOtherSessions > 0 ? " by this session's edits" : "";
            text.Append($"fuse: no errors introduced{whose} ({report.FilesChecked} file(s) checked{summaryTail})");
            return new OperationResult(Outcome.Clean, text.ToString());
        }

        // The counts are the engine's, over every error it found; the errors it sends are capped, and so are those printed.
        var shown = Math.Min(MaxShown, report.Errors.Length);
        var more = report.ErrorCount > shown ? $", first {shown} shown" : "";
        text.Append($"fuse: {report.ErrorCount} error(s) introduced in {report.ErrorFileCount} file(s){more} ({string.Join(", ", report.Projects)}){summaryTail}");
        return new OperationResult(Outcome.ProblemsFound, text.ToString());
    }
}
