using Fuse.Client;
using Fuse.Failures;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Check;
using Fuse.Protocol.Requests;
using Fuse.Repo.Tracking;
namespace Fuse.Operations.Check;

/// <summary>Asks the engine for the errors introduced since HEAD and renders them.</summary>
internal static class CheckOperation
{
    /// <param name="root">The repository the engine checks.</param>
    /// <param name="invocation">What to check, whose edits it answers, and how to show it.</param>
    /// <param name="timeout">How long to wait for the answer.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public static async Task<(OperationResult Result, EngineResponse Response)> RunAsync(
        RepoRoot root, CheckInvocation invocation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        EngineRequest request = invocation.Files is null
            ? new EngineRequest.CheckChanges(invocation.WaitForLoad) { Session = invocation.Session?.StorageKey, Mode = invocation.Session is not null ? SessionCheckMode.Stop : SessionCheckMode.Check, MaxErrors = RequestedFor(invocation.Limit.ToCount(), invocation.Json), StampTree = invocation.IncludeTreeStamp, Brief = invocation.Brief }
            : new EngineRequest.CheckFiles(invocation.Files, invocation.WaitForLoad) { Session = invocation.Session?.StorageKey, LeaveOutCompilationWide = !invocation.CompilationWideAnalyzers, MaxErrors = RequestedFor(invocation.Limit.ToCount(), invocation.Json), StampTree = invocation.IncludeTreeStamp, Brief = invocation.Brief, InteractiveFast = invocation.InteractiveFast };
        var response = await EngineClient.SendAsync(root, request, timeout, cancellationToken).ConfigureAwait(false);
        if (response is not EngineResponse.CheckAnswered answered)
            return (OperationResult.Unanswered(response, "check result"), response);
        var treeStamp = StampOf(answered.Report);
        return (Render(answered.Report, ResultsFile.WriteCheck(root, answered.Report, treeStamp), ShownFor(invocation.Limit.ToCount(), invocation.Json), invocation.Json, treeStamp, invocation.IncludeNonCSharp, invocation.Brief, root.Path), response);
    }

    /// <param name="specs">Absolute file and directory paths and absolute glob patterns, in the order named.</param>
    /// <param name="maxErrors">How many errors to show: 0 for the default count, <see cref="int.MaxValue"/> for every error.</param>
    /// <param name="json">True for one JSON object per error instead of text.</param>
    /// <param name="timeout">How long to wait for the answer.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <param name="includeTreeStamp">True to stamp the summary and the results file with the tree the check ended on, for <c>fuse check</c>; hook and MCP answers stay unstamped.</param>
    /// <param name="session">The session the check is answered to (<c>FUSE_SESSION</c>), or null to report every error.</param>
    /// <param name="brief">True to request and render Roslyn repair packets (<c>--brief</c>).</param>
    public static async Task<(OperationResult Result, EngineResponse Response)> RunScopedAsync(
        RepoRoot root, IReadOnlyList<string> specs, int maxErrors, bool json, TimeSpan timeout, CancellationToken cancellationToken, bool includeTreeStamp = false, string? session = null, bool brief = false)
    {
        var request = new EngineRequest.CheckPaths([.. specs], WaitForLoad: true) { MaxErrors = RequestedFor(maxErrors, json), StampTree = includeTreeStamp, Session = session, Brief = brief };
        var response = await EngineClient.SendAsync(root, request, timeout, cancellationToken).ConfigureAwait(false);
        if (response is not EngineResponse.CheckAnswered answered)
            return (OperationResult.Unanswered(response, "check result"), response);
        var treeStamp = StampOf(answered.Report);
        return (Render(answered.Report, ResultsFile.WriteCheck(root, answered.Report, treeStamp), ShownFor(maxErrors, json), json, treeStamp, brief: brief, repoRootPath: root.Path), response);
    }

    /// <summary>How many errors the request asks the engine to send: every error for JSON and <c>--all-errors</c>, else <paramref name="maxErrors"/>.</summary>
    private static int RequestedFor(int maxErrors, bool json) =>
        json || maxErrors == int.MaxValue ? int.MaxValue : maxErrors;

    /// <summary>How many errors the answer prints: every error for JSON and <c>--all-errors</c>, else <paramref name="maxErrors"/>, or the default count when it is 0.</summary>
    private static int ShownFor(int maxErrors, bool json) =>
        json || maxErrors == int.MaxValue ? int.MaxValue : maxErrors > 0 ? maxErrors : CheckRenderer.MaxShown;

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
    internal static string GeneratedLine(string file) => CheckRenderer.GeneratedLine(file);

    /// <summary>One error as the answer prints it: its canonical line, its cause, the generated-code line, and any repair packet.</summary>
    /// <param name="showCause">False for a grouped member, whose group header already names the cause.</param>
    internal static void AppendError(System.Text.StringBuilder text, ReportedError reported, HashSet<string> generatedShown, bool showCause = true, bool brief = false, string? repoRootPath = null) =>
        CheckRenderer.AppendError(text, reported, generatedShown, showCause, brief, repoRootPath);

    internal static string? ReadSourceWindow(string repoRootPath, string relativePath, int errorLine) =>
        CheckRenderer.ReadSourceWindow(repoRootPath, relativePath, errorLine);

    /// <param name="maxShown">How many errors to print, or <see cref="int.MaxValue"/> for every error.</param>
    /// <param name="json">True for one JSON object per error instead of text, with the summary as the last line.</param>
    /// <param name="treeStamp">The stamp a <c>fuse check</c> summary carries, or null to print none.</param>
    /// <param name="includeNonCSharp">False to leave the changed files Fuse does not check out of the summary.</param>
    /// <param name="brief">True to render <see cref="ReportedRepairPacket"/> details (and fallback source windows).</param>
    /// <param name="repoRootPath">Repository root used for fallback source windows when <paramref name="brief"/> is true.</param>
    internal static OperationResult Render(CheckReport report, string? resultsPath = null, int maxShown = CheckRenderer.MaxShown, bool json = false, string? treeStamp = null, bool includeNonCSharp = true, bool brief = false, string? repoRootPath = null) =>
        CheckRenderer.Render(report, resultsPath, maxShown, json, treeStamp, includeNonCSharp, brief, repoRootPath);
}
