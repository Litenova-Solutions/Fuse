using System.Text;
using Fuse.Dotnet.Build;
using Fuse.Dotnet.Processes;
using Fuse.Paths;
namespace Fuse.Operations.Build;

/// <summary>Renders a finished <c>dotnet build</c> the way the console answer prints it: the errors, the lock and refresh lines under them, the warnings, and the summary.</summary>
internal static class BuildRenderer
{
    /// <param name="context">The finished build, where it ran, how long it took, and how to show it.</param>
    internal static OperationResult Render(BuildRenderContext context)
    {
        var diagnostics = BuildOutputParser.Diagnostics(context.Result.Output, context.Root);
        List<(string Command, List<string> Lines)> commands = context.Log.Length == 0 ? [] : BuildLog.FailedCommands(context.Log);
        List<BuildWarning> warnings = context.Options.ListWarnings && context.Log.Length > 0 ? ListedWarnings(BuildLog.WarningsList(context.Log, context.Root), diagnostics) : [];
        int? warningsCount = context.Log.Length == 0 ? null : BuildLog.Warnings(context.Log);
        var times = context.Seconds >= BuildOperation.TimesFromSeconds && context.Log.Length > 0 ? BuildLog.Times(context.Log) : null;
        return RenderCore(context.Root, diagnostics, commands, warnings, warningsCount, times, context.Result.ExitCode, BuildOperation.Tail(context.Result.Output), context.Seconds, context.Operation, context.Options, context.ResultsPath, context.TreeStamp, context.FileTotal, context.RestoreTarget, context.Passthrough, context.Changed);
    }

    /// <summary>Renders parsed diagnostics the way a fresh build prints them: the errors, the lock and refresh lines under them, the warnings, and the summary.</summary>
    /// <param name="root">The repository the errors print relative to.</param>
    /// <param name="diagnostics">The errors in first-seen order.</param>
    /// <param name="commands">Each failed command with the last lines it printed.</param>
    /// <param name="warnings">The warnings that are not also errors, listed only with <c>--warnings</c>.</param>
    /// <param name="warningsCount">How many warnings the build produced, or null when its log was not read.</param>
    /// <param name="times">Where the build's time went, or null when it is not said.</param>
    /// <param name="exitCode">The code the build exited with.</param>
    /// <param name="tail">The last lines of the build's output, for a failure without an error line.</param>
    /// <param name="seconds">How long the build took.</param>
    /// <param name="operation">What ran, as the summary names it.</param>
    /// <param name="options">How many errors to print, in what form, and whether the warnings are listed.</param>
    /// <param name="resultsPath">Where every error and warning was written, or null when it was not written.</param>
    /// <param name="treeStamp">The stamp a <c>fuse build</c> summary carries, or null to print none.</param>
    /// <param name="fileTotal">How many errors the results file holds when it holds more than this build's, or null.</param>
    /// <param name="restoreTarget">What a lock refresh restores, relative to the root, or null to derive it from the repository.</param>
    /// <param name="passthrough">The arguments for <c>dotnet build</c>, unchanged, or null when the <c>--all-errors</c> hint names none.</param>
    /// <param name="changed">True when the build covers the changed projects, so the <c>--all-errors</c> hint keeps <c>--changed</c>.</param>
    internal static OperationResult RenderCore(string root, List<BuildDiagnostic> diagnostics, IReadOnlyList<(string Command, List<string> Lines)> commands, List<BuildWarning> warnings, int? warningsCount, string? times, int exitCode, string tail, double seconds, string operation, BuildOptions options, string? resultsPath, string? treeStamp, int? fileTotal, string? restoreTarget, IReadOnlyList<string>? passthrough = null, bool changed = false)
    {
        var text = new StringBuilder();
        if (options.Json)
        {
            foreach (var diagnostic in diagnostics)
                text.Append(DiagnosticJson.BuildLine(diagnostic, LockLine.Holder(diagnostic))).Append('\n');
            // With no diagnostic parsed, the tail says where the failure is, as in text mode.
            if (diagnostics.Count == 0 && exitCode != 0)
                text.Append(tail).Append('\n');
        }
        else if (diagnostics.Count > 0 || exitCode == 0)
        {
            var refreshPrinted = false;
            foreach (var diagnostic in diagnostics.Take(options.MaxShown))
            {
                var line = diagnostic.ToString();
                text.Append(line).Append('\n');
                // What a failed command printed explains its exit code, which is all the error line says.
                foreach (var (command, lines) in commands)
                {
                    if (!line.Contains($"error MSB3073: The command \"{command}", StringComparison.Ordinal))
                        continue;
                    foreach (var commandLine in lines)
                        text.Append("  ").Append(commandLine).Append('\n');
                    break;
                }

                // A file another process holds open names the holder and what to do.
                if (LockLine.For(diagnostic, root) is { } locked)
                    text.Append("  ").Append(locked).Append('\n');

                // A package the feed repacked without a new version names the one restore that refreshes the lock files, once.
                if (!refreshPrinted && diagnostic.IsContentHashFailure && LockRefreshLine(root, restoreTarget) is { } refresh)
                {
                    text.Append("  ").Append(refresh).Append('\n');
                    refreshPrinted = true;
                }
            }
        }
        else
        {
            // Nothing matched the diagnostic format: show the tail, which is where MSBuild puts the failure.
            text.Append(tail).Append('\n');
        }

        if (options.ListWarnings)
            AppendWarnings(text, warnings, resultsPath);

        var timesTail = times is null ? "" : $" ({times})";
        var warningsTail = warningsCount is null ? "" : $"; {warningsCount} warning(s)";
        if (exitCode == 0)
        {
            text.Append($"fuse: {operation} succeeded{Duration.In(seconds)}{warningsTail}{timesTail}{treeStamp}");
            return new OperationResult(Outcome.Clean, text.ToString());
        }

        if (diagnostics.Count == 0)
        {
            text.Append($"fuse: {operation} failed (exit code {exitCode}){Duration.In(seconds)}{warningsTail}{timesTail}{treeStamp}");
            return new OperationResult(Outcome.ProblemsFound, text.ToString());
        }

        var shown = options.Json ? diagnostics.Count : Math.Min(options.MaxShown, diagnostics.Count);
        var capped = !options.Json && diagnostics.Count > shown;
        var more = capped ? $", first {shown} shown" : "";
        // The file holds every error; the answer names it only when it left errors out, and never in JSON, which already holds them.
        var file = capped && resultsPath is not null ? $"; all {fileTotal ?? diagnostics.Count} in {resultsPath}" : "";
        // The hinted command keeps the original arguments, so it reuses this finished build instead of building again.
        var hint = capped ? $"; {BuildOperation.AllErrorsHint(passthrough ?? [], changed)} for every error" : "";
        text.Append($"fuse: {operation} failed with {diagnostics.Count} error(s){more}{Duration.In(seconds)}{warningsTail}{timesTail}{treeStamp}{hint}{file}");
        return new OperationResult(Outcome.ProblemsFound, text.ToString());
    }

    /// <summary>The warnings that are not also errors: one treated as an error already prints as one.</summary>
    internal static List<BuildWarning> ListedWarnings(IReadOnlyList<BuildWarning> warnings, IReadOnlyList<BuildDiagnostic> diagnostics)
    {
        var errors = diagnostics.Select(d => (d.File, d.Line, d.Column, d.Code)).ToHashSet();
        return [.. warnings.Where(w => !errors.Contains((w.File, (int?)w.Line, (int?)w.Column, w.Code)))];
    }

    /// <summary>The warnings after the errors, each indented, with where the rest are when they do not all fit.</summary>
    internal static void AppendWarnings(StringBuilder text, List<BuildWarning> warnings, string? resultsPath)
    {
        foreach (var warning in warnings.Take(BuildOperation.MaxWarningsShown))
            text.Append("  ").Append(warning).Append('\n');
        if (warnings.Count > BuildOperation.MaxWarningsShown)
            text.Append("  ... ").Append(warnings.Count - BuildOperation.MaxWarningsShown).Append(" more warning(s)").Append(resultsPath is null ? " not shown" : $" in {resultsPath}").Append('\n');
    }

    /// <summary>
    ///     The line under a package content hash failure: the one restore that refreshes the lock files and the files it
    ///     refreshes. The caller runs nothing; the user runs the command.
    /// </summary>
    /// <param name="root">The repository the lock files are listed relative to.</param>
    /// <param name="restoreTarget">What to restore, relative to the root, or null to derive it from the repository.</param>
    internal static string LockRefreshLine(string root, string? restoreTarget)
    {
        var target = restoreTarget ?? DiscoveredSolution(root);
        var command = target is null ? "dotnet restore --force-evaluate" : $"dotnet restore {target} --force-evaluate";
        var locks = LockFiles(root);
        if (locks.Count == 0)
            return $"lock file: run {command} to refresh the restore";
        var shown = string.Join(", ", locks.Take(BuildOperation.MaxLockFilesShown));
        var more = locks.Count > BuildOperation.MaxLockFilesShown ? $" and {locks.Count - BuildOperation.MaxLockFilesShown} more" : "";
        return $"lock file: run {command} to refresh {shown}{more}";
    }

    /// <summary>The repository's solution when it holds exactly one, relative to the root, or null.</summary>
    internal static string? DiscoveredSolution(string root)
    {
        try
        {
            var solutions = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                .Where(file => file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OfType<string>()
                .OrderBy(name => name, StringComparer.Ordinal)
                .Take(2)
                .ToList();
            return solutions.Count == 1 ? solutions[0] : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Every lock file under the root, repository-relative with forward slashes, in ordinal order.</summary>
    internal static List<string> LockFiles(string root)
    {
        var locks = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "packages.lock.json", SearchOption.AllDirectories))
            {
                if (PathRules.IsBuildOutput(root, file))
                    continue;
                locks.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        locks.Sort(StringComparer.Ordinal);
        return locks;
    }
}
