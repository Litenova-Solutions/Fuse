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

    /// <summary>Runs a check.</summary>
    /// <param name="root">The repository.</param>
    /// <param name="files">Absolute paths to scope to, or null for every change.</param>
    /// <param name="waitForLoad">Wait for the engine to finish loading, or return immediately with <see cref="ErrorCode.Loading"/>.</param>
    /// <param name="timeout">How long to wait for the answer.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public static async Task<(OperationResult Result, EngineResponse Response)> RunAsync(
        RepoRoot root, IReadOnlyList<string>? files, bool waitForLoad, TimeSpan timeout, CancellationToken cancellationToken)
    {
        EngineRequest request = files is null ? new EngineRequest.CheckChanges(waitForLoad) : new EngineRequest.CheckFiles(files, waitForLoad);
        var response = await EngineClient.SendAsync(root, request, timeout, cancellationToken).ConfigureAwait(false);
        if (response is not EngineResponse.CheckAnswered answered)
            return (new OperationResult(Outcome.Unanswered, $"fuse: {(response as EngineResponse.Unanswered)?.Message ?? "the engine gave no answer"}"), response);
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

    internal static OperationResult Render(CheckReport report)
    {
        var text = new StringBuilder();
        foreach (var reported in report.Errors.Take(MaxShown))
        {
            text.Append(reported.Error).Append('\n');
            // The line under an error in a file the agent did not edit says which of its edits put the file in scope.
            if (reported.Cause is { } cause)
                text.Append("  ").Append(cause.Kind == CauseKind.Removed ? "removed" : "changed").Append(": ").Append(cause.Declaration).Append('\n');
        }

        var scope = new List<string>();
        if (report.DeclarationsChangedIn.Length > 0)
            scope.Add($"{string.Join(", ", report.DeclarationsChangedIn)} declarations changed, {report.DependentProjectsChecked} dependent project(s) checked");
        if (report.CheckedWholeProjects)
            scope.Add("checked whole projects");
        // Counted among the printed errors only, since a cause left out of an error that is not printed is no loss.
        var causesLeftOut = report.Errors.Take(MaxShown).Count(e => e.IsCauseLeftOut);
        if (causesLeftOut > 0)
            scope.Add($"{causesLeftOut} cause(s) left out");
        var scopeText = scope.Count > 0 ? "; " + string.Join("; ", scope) : "";

        if (report.Errors.Length == 0)
        {
            text.Append($"fuse: no errors introduced ({report.FilesChecked} file(s) checked{scopeText})");
            return new OperationResult(Outcome.Clean, text.ToString());
        }

        var files = report.Errors.Select(e => e.Error.Path).Distinct().Count();
        var more = report.Errors.Length > MaxShown ? $", first {MaxShown} shown" : "";
        text.Append($"fuse: {report.Errors.Length} error(s) introduced in {files} file(s){more} ({string.Join(", ", report.Projects)}){scopeText}");
        return new OperationResult(Outcome.ProblemsFound, text.ToString());
    }
}
