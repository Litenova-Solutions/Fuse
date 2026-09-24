using System.Text;
using Fuse.Engine;
using Fuse.Protocol;
using Fuse.Repo;

namespace Fuse.Cli;

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
        var response = await EngineClient.SendAsync(root, new EngineRequest("", RequestKind.Check, files?.ToArray(), wait), timeout, cancellationToken).ConfigureAwait(false);
        if (response.Status != ResponseStatus.Ok || response.Check is null)
            return (new OperationResult(2, $"fuse: {response.Message ?? "the engine gave no answer"}"), response);
        return (Render(response.Check), response);
    }

    internal static OperationResult Render(CheckReport report)
    {
        var text = new StringBuilder();
        foreach (var diagnostic in report.Introduced.Take(MaxShown))
            text.Append(diagnostic).Append('\n');
        var scope = new List<string>();
        if (report.SurfaceChangedIn.Length > 0)
            scope.Add($"{string.Join(", ", report.SurfaceChangedIn)} declarations changed, {report.DependentProjectsChecked} dependent project(s) checked");
        if (report.WholeProjects)
            scope.Add("whole projects bound");
        var scopeText = scope.Count > 0 ? "; " + string.Join("; ", scope) : "";

        if (report.Introduced.Length == 0)
        {
            text.Append($"fuse: no new errors ({report.FilesChecked} file(s) checked{scopeText})");
            return new OperationResult(0, text.ToString());
        }

        var files = report.Introduced.Select(d => d.Path).Distinct().Count();
        var more = report.Introduced.Length > MaxShown ? $", first {MaxShown} shown" : "";
        text.Append($"fuse: {report.Introduced.Length} new error(s) in {files} file(s){more} ({string.Join(", ", report.Projects)}){scopeText}");
        return new OperationResult(1, text.ToString());
    }
}
