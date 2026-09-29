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
    /// <param name="wait">Wait for the engine to finish loading, or return immediately with <see cref="ErrorCode.Loading"/>.</param>
    /// <param name="timeout">How long to wait for the answer.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public static async Task<(OperationResult Result, EngineResponse Response)> RunAsync(
        RepoRoot root, IReadOnlyList<string>? files, bool wait, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var response = await EngineClient.SendAsync(root, new EngineRequest("", RequestKind.Check, Files: files?.ToArray(), Wait: wait), timeout, cancellationToken).ConfigureAwait(false);
        if (response.Status != ResponseStatus.Ok || response.Check is null)
            return (new OperationResult(Outcome.Unanswered, $"fuse: {response.Message ?? "the engine gave no answer"}"), response);
        return (Render(response.Check), response);
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
        if (report.CausesLeftOut > 0)
            scope.Add($"{report.CausesLeftOut} cause(s) left out");
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
