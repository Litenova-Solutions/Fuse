using System.Text;
using Fuse.Dotnet;
using Fuse.Paths;

namespace Fuse.Operations;

/// <summary>The full results of the last build, which the console answer caps.</summary>
internal static class BuildResultsFile
{
    /// <summary>
    ///     Every error and warning of a build, as <c>last-build-results.txt</c> in the state directory, for the agent to
    ///     read whole, and with <paramref name="json"/> the errors again as JSON lines in <c>last-build-results.json</c>.
    ///     Always replaces the previous files, even when the build found nothing. Null when the text file could not be written.
    /// </summary>
    /// <param name="treeStamp">The stamp the header carries, or null for none.</param>
    internal static string? WriteBuild(RepoRoot root, IReadOnlyList<BuildDiagnostic> errors, IReadOnlyList<BuildWarning> warnings, bool json, string? treeStamp = null)
    {
        var text = new StringBuilder();
        text.Append($"fuse build: {errors.Count} error(s){treeStamp}").Append('\n');
        foreach (var error in errors)
            text.Append(error).Append('\n');
        if (warnings.Count > 0)
        {
            text.Append("## warnings").Append('\n');
            foreach (var warning in warnings)
                text.Append(warning).Append('\n');
        }

        var path = ResultsFile.Write(root, "last-build-results.txt", text.ToString());
        if (json)
            ResultsFile.Write(root, "last-build-results.json", errors.Count == 0 ? "" : string.Join('\n', errors.Select(e => DiagnosticJson.BuildLine(e))) + "\n");
        return path;
    }
}
