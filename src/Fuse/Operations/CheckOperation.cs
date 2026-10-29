using System.Text;
using Fuse.Engine.Client;
using Fuse.Failures;
using Fuse.Paths;
using Fuse.Protocol;
using Fuse.Repo;

namespace Fuse.Operations;

/// <summary>Asks the engine for the errors introduced since HEAD and renders them.</summary>
internal static class CheckOperation
{
    private const int MaxShown = 20;

    /// <summary>At most this many individual cause lines per text answer; group headers are not counted.</summary>
    internal const int MaxCauseLines = 10;

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
    /// <param name="maxErrors">How many errors to show: 0 for the default count, <see cref="int.MaxValue"/> for every error.</param>
    /// <param name="json">True for one JSON object per error instead of text.</param>
    /// <param name="includeTreeStamp">True to stamp the summary and the results file with the tree the check ended on, for <c>fuse check</c>; hook and MCP answers stay unstamped.</param>
    /// <param name="includeNonCSharp">False to leave the changed files Fuse does not check out of the summary; the post-edit hook sends false.</param>
    public static async Task<(OperationResult Result, EngineResponse Response)> RunAsync(
        RepoRoot root, IReadOnlyList<string>? files, string? session, bool waitForLoad, bool compilationWideAnalyzers, TimeSpan timeout, CancellationToken cancellationToken, int maxErrors = 0, bool json = false, bool includeTreeStamp = false, bool includeNonCSharp = true)
    {
        EngineRequest request = files is null
            ? new EngineRequest.CheckChanges(waitForLoad) { Session = session, IsStopping = session is not null, MaxErrors = RequestedFor(maxErrors, json), StampTree = includeTreeStamp }
            : new EngineRequest.CheckFiles(files, waitForLoad) { Session = session, LeaveOutCompilationWide = !compilationWideAnalyzers, MaxErrors = RequestedFor(maxErrors, json), StampTree = includeTreeStamp };
        var response = await EngineClient.SendAsync(root, request, timeout, cancellationToken).ConfigureAwait(false);
        if (response is not EngineResponse.CheckAnswered answered)
            return (OperationResult.Unanswered(response, "check result"), response);
        var treeStamp = StampOf(answered.Report);
        return (Render(answered.Report, ResultsFile.WriteCheck(root, answered.Report, treeStamp), ShownFor(maxErrors, json), json, treeStamp, includeNonCSharp), response);
    }

    /// <param name="specs">Absolute file and directory paths and absolute glob patterns, in the order named.</param>
    /// <param name="maxErrors">How many errors to show: 0 for the default count, <see cref="int.MaxValue"/> for every error.</param>
    /// <param name="json">True for one JSON object per error instead of text.</param>
    /// <param name="timeout">How long to wait for the answer.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <param name="includeTreeStamp">True to stamp the summary and the results file with the tree the check ended on, for <c>fuse check</c>; hook and MCP answers stay unstamped.</param>
    public static async Task<(OperationResult Result, EngineResponse Response)> RunScopedAsync(
        RepoRoot root, IReadOnlyList<string> specs, int maxErrors, bool json, TimeSpan timeout, CancellationToken cancellationToken, bool includeTreeStamp = false)
    {
        var request = new EngineRequest.CheckPaths([.. specs], WaitForLoad: true) { MaxErrors = RequestedFor(maxErrors, json), StampTree = includeTreeStamp };
        var response = await EngineClient.SendAsync(root, request, timeout, cancellationToken).ConfigureAwait(false);
        if (response is not EngineResponse.CheckAnswered answered)
            return (OperationResult.Unanswered(response, "check result"), response);
        var treeStamp = StampOf(answered.Report);
        return (Render(answered.Report, ResultsFile.WriteCheck(root, answered.Report, treeStamp), ShownFor(maxErrors, json), json, treeStamp), response);
    }

    /// <summary>How many errors the request asks the engine to send: every error for JSON and <c>--all-errors</c>, else <paramref name="maxErrors"/>.</summary>
    private static int RequestedFor(int maxErrors, bool json) =>
        json || maxErrors == int.MaxValue ? int.MaxValue : maxErrors;

    /// <summary>How many errors the answer prints: every error for JSON and <c>--all-errors</c>, else the default count.</summary>
    private static int ShownFor(int maxErrors, bool json) =>
        json || maxErrors == int.MaxValue ? int.MaxValue : maxErrors > 0 ? maxErrors : MaxShown;

    /// <summary>The stamp the engine put on a finished check for the command line, or null for a hook or MCP answer, which asks for none.</summary>
    private static string? StampOf(CheckReport report) =>
        report.Tree is { } tree ? TreeStamp.SummaryPart(tree, report.TreeFiles) : null;

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

    /// <summary>One error as the answer prints it: its canonical line, its cause, and the generated-code line once per file.</summary>
    /// <param name="showCause">False for a grouped member, whose group header already names the cause.</param>
    internal static void AppendError(StringBuilder text, ReportedError reported, HashSet<string> generatedShown, bool showCause = true)
    {
        text.Append(reported.Error).Append('\n');
        // The line under an error in a file the agent did not edit names the declaration change that made the file a candidate.
        if (showCause && reported.Cause is { } cause)
            text.Append("  ").Append(cause.Kind == CauseKind.Removed ? "removed" : "changed").Append(": ").Append(cause.Declaration).Append('\n');
        // Said once per file: the file may be older than the code it is generated from.
        if (reported.GeneratedFile is { } generated && generatedShown.Add(generated))
            text.Append("  ").Append(GeneratedLine(generated)).Append('\n');
    }

    /// <param name="maxShown">How many errors to print, or <see cref="int.MaxValue"/> for every error.</param>
    /// <param name="json">True for one JSON object per error instead of text, with the summary as the last line.</param>
    /// <param name="treeStamp">The stamp a <c>fuse check</c> summary carries, or null to print none.</param>
    /// <param name="includeNonCSharp">False to leave the changed files Fuse does not check out of the summary.</param>
    internal static OperationResult Render(CheckReport report, string? resultsPath = null, int maxShown = MaxShown, bool json = false, string? treeStamp = null, bool includeNonCSharp = true)
    {
        var text = new StringBuilder();
        IReadOnlyList<(string? Header, ReportedError Error, bool ShowCause)> shown;
        if (json)
        {
            foreach (var reported in report.Errors)
                text.Append(DiagnosticJson.CheckLine(reported)).Append('\n');
            shown = [.. report.Errors.Select(e => ((string?)null, e, false))];
        }
        else
        {
            shown = CheckGroups.Split(report.Errors, maxShown <= 0 ? MaxShown : maxShown);
            var generatedShown = new HashSet<string>(StringComparer.Ordinal);
            var causeLines = 0;
            foreach (var (header, error, showCause) in shown)
            {
                if (header is not null)
                    text.Append(header).Append('\n');
                var show = showCause && error.Cause is not null && causeLines < MaxCauseLines;
                if (showCause && error.Cause is not null && show)
                    causeLines++;
                AppendError(text, error, generatedShown, show);
            }
        }

        var summaryParts = new List<string>();
        if (report.DeclarationsChangedIn.Length > 0)
            summaryParts.Add($"{string.Join(", ", report.DeclarationsChangedIn)} declarations changed, {report.DependentProjectsChecked} dependent project(s) checked");
        if (report.CheckedWholeProjects)
            summaryParts.Add(WholeProjectsSummary(report.WholeProjects));
        // Counted among the printed errors only, since a cause left out of an error that is not printed is no loss.
        // The engine sends every cause; the text answer prints at most MaxCauseLines cause lines past the group headers.
        var causesLeftOut = shown.Count(e => e.Error.IsCauseLeftOut)
            + Math.Max(0, shown.Count(e => e.ShowCause && e.Error.Cause is not null) - MaxCauseLines);
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
        if (includeNonCSharp && report.NonCSharpChanged > 0)
            summaryParts.Add($"{report.NonCSharpChanged} non-C# file(s) changed, which Fuse does not check: {NonCSharpSample(report)}");
        var summaryTail = summaryParts.Count > 0 ? "; " + string.Join("; ", summaryParts) : "";
        var scoped = report.ScopedPaths.Length > 0 ? string.Join(", ", report.ScopedPaths) : null;

        if (report.ErrorCount == 0)
        {
            var whose = report.LeftToOtherSessions > 0 ? " by this session's edits" : "";
            var where = scoped is null ? "" : $" in {scoped} and what it reaches";
            text.Append($"fuse: no errors introduced{whose}{where} ({report.FilesChecked} file(s) checked{summaryTail}{treeStamp})");
            return new OperationResult(Outcome.Clean, text.ToString());
        }

        // The counts are the engine's, over every error it found; the errors it sends are capped, and so are those printed.
        var printed = shown.Count;
        var more = report.ErrorCount > printed ? $", first {printed} shown" : "";
        // The file holds every error the engine sent; the answer names it only when it left errors out, and never in JSON, which already holds them.
        var file = !json && report.ErrorCount > printed && resultsPath is not null ? $"; all {report.ErrorCount} in {resultsPath}" : "";
        var scope = scoped is null ? "" : $"; checked {scoped} and what it reaches";
        text.Append($"fuse: {report.ErrorCount} error(s) introduced in {report.ErrorFileCount} file(s){more} ({string.Join(", ", report.Projects)}){summaryTail}{scope}{treeStamp}{file}");
        return new OperationResult(Outcome.ProblemsFound, text.ToString());
    }

    /// <summary>The changed files the summary names for what Fuse does not check: the first five of the sample, then how many more changed.</summary>
    private static string NonCSharpSample(CheckReport report)
    {
        var shown = report.NonCSharpSample.Take(5).ToList();
        if (report.NonCSharpChanged <= shown.Count)
            return string.Join(", ", shown);
        return shown.Count == 0
            ? $"... and {report.NonCSharpChanged} more"
            : $"{string.Join(", ", shown)}, ... and {report.NonCSharpChanged - shown.Count} more";
    }

    private static string WholeProjectsSummary(ReportedWholeProject[] whole)
    {
        if (whole.Length == 0)
            return "checked whole projects";
        var shown = whole.OrderBy(w => w.Project, StringComparer.Ordinal).Take(3).ToList();
        var parts = new List<string>();
        var filesOmitted = 0;
        foreach (var project in shown)
        {
            var files = project.ChangedFiles.Order(StringComparer.Ordinal).ToList();
            var causes = project.Causes.Order(StringComparer.Ordinal).ToList();
            if (files.Count == 0)
            {
                parts.Add(project.Project);
                continue;
            }
            filesOmitted += Math.Max(0, files.Count - 2);
            var filesPart = string.Join(", ", files.Take(2));
            parts.Add(causes.Count == 0 ? $"{project.Project} because {filesPart}" : $"{project.Project} because {filesPart} ({string.Join("; ", causes)})");
        }
        var omitted = (whole.Length - shown.Count) + filesOmitted;
        return "checked whole projects: " + string.Join("; ", parts) + (omitted > 0 ? $"; and {omitted} more in the engine log" : "");
    }
}
