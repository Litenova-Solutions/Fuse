using System.Text;
using Fuse.Protocol.Check;
namespace Fuse.Operations.Check;

/// <summary>Renders a check report as text or JSON lines with its summary.</summary>
internal static class CheckRenderer
{
    /// <summary>How many errors the text answer prints when the caller names no count.</summary>
    internal const int MaxShown = 20;

    /// <summary>At most this many individual cause lines per text answer; group headers are not counted.</summary>
    internal const int MaxCauseLines = 10;

    /// <summary>The line under an error about something a file the build generates declares.</summary>
    internal static string GeneratedLine(string file) =>
        $"generated: {file} declares it, and Fuse reads that file as the last build wrote it; if it is generated from changed code, run fuse build to write it again";

    /// <summary>One error as the answer prints it: its canonical line, its cause, the generated-code line, and any repair packet.</summary>
    /// <param name="showCause">False for a grouped member, whose group header already names the cause.</param>
    internal static void AppendError(StringBuilder text, ReportedError reported, HashSet<string> generatedShown, bool showCause = true, bool brief = false, string? repoRootPath = null)
    {
        text.Append(reported.Error).Append('\n');
        // The line under an error in a file the agent did not edit names the declaration change that made the file a candidate.
        if (showCause && reported.Cause is { } cause)
            text.Append("  ").Append(cause.Kind == CauseKind.Removed ? "removed" : "changed").Append(": ").Append(cause.Declaration).Append('\n');
        // Said once per file: the file may be older than the code it is generated from.
        if (reported.GeneratedFile is { } generated && generatedShown.Add(generated))
            text.Append("  ").Append(GeneratedLine(generated)).Append('\n');
        if (brief || reported.Repair is not null)
            AppendRepair(text, EnsureRepairPacket(reported, brief, repoRootPath).Repair);
    }

    private static ReportedError EnsureRepairPacket(ReportedError reported, bool brief, string? repoRootPath)
    {
        if (!brief || reported.Repair is not null || repoRootPath is null)
            return reported;
        var window = ReadSourceWindow(repoRootPath, reported.Error.Path, reported.Error.Line);
        return window is null ? reported : reported with { Repair = new ReportedRepairPacket(SourceWindow: window) };
    }

    private static void AppendRepair(StringBuilder text, ReportedRepairPacket? repair)
    {
        if (repair is null)
            return;
        if (repair.SuggestedUsings.Length > 0)
            text.Append("  using: ").Append(string.Join(" ", repair.SuggestedUsings)).Append('\n');
        if (repair.Candidates.Length > 0)
        {
            text.Append("  candidates:\n");
            foreach (var candidate in repair.Candidates)
                text.Append("    ").Append(candidate).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(repair.InterfaceStub))
        {
            text.Append("  stub:\n");
            foreach (var line in repair.InterfaceStub.Split('\n'))
                text.Append("    ").Append(line.TrimEnd('\r')).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(repair.SourceWindow))
        {
            text.Append("  source:\n");
            foreach (var line in repair.SourceWindow.Split('\n'))
                text.Append("    ").Append(line.TrimEnd('\r')).Append('\n');
        }
    }

    internal static string? ReadSourceWindow(string repoRootPath, string relativePath, int errorLine)
    {
        if (errorLine <= 0 || string.IsNullOrWhiteSpace(relativePath))
            return null;
        try
        {
            var fullPath = Path.Combine(repoRootPath, relativePath);
            if (!File.Exists(fullPath))
                return null;
            var lines = File.ReadAllLines(fullPath);
            if (lines.Length == 0)
                return null;
            var start = Math.Max(1, errorLine - 2);
            var end = Math.Min(lines.Length, errorLine + 2);
            var sb = new StringBuilder();
            for (var line = start; line <= end; line++)
            {
                if (sb.Length > 0)
                    sb.Append('\n');
                var marker = line == errorLine ? ">" : " ";
                sb.Append($"{marker} {line,4}: {lines[line - 1]}");
            }

            return sb.ToString();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <param name="maxShown">How many errors to print, or <see cref="int.MaxValue"/> for every error.</param>
    /// <param name="json">True for one JSON object per error instead of text, with the summary as the last line.</param>
    /// <param name="treeStamp">The stamp a <c>fuse check</c> summary carries, or null to print none.</param>
    /// <param name="includeNonCSharp">False to leave the changed files Fuse does not check out of the summary.</param>
    /// <param name="brief">True to render <see cref="ReportedRepairPacket"/> details (and fallback source windows).</param>
    /// <param name="repoRootPath">Repository root used for fallback source windows when <paramref name="brief"/> is true.</param>
    internal static OperationResult Render(CheckReport report, string? resultsPath = null, int maxShown = MaxShown, bool json = false, string? treeStamp = null, bool includeNonCSharp = true, bool brief = false, string? repoRootPath = null)
    {
        var text = new StringBuilder();
        IReadOnlyList<(string? Header, ReportedError Error, bool ShowCause)> shown;
        if (json)
        {
            foreach (var reported in report.Errors)
                text.Append(DiagnosticJson.CheckLine(EnsureRepairPacket(reported, brief, repoRootPath))).Append('\n');
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
                AppendError(text, error, generatedShown, show, brief, repoRootPath);
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
