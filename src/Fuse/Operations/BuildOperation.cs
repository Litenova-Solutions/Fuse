using System.Text;
using System.Text.RegularExpressions;
using Fuse.Dotnet;
using Fuse.Paths;

namespace Fuse.Operations;

/// <summary>
///     Runs the real <c>dotnet build</c> and prints its errors, or the end of its output when no error line parses. Under
///     an error for a command that exited with a code other than 0, it prints the last lines the command printed, and a
///     build of 10 seconds or more says where its time went; both come from the file log it adds (<see cref="BuildLog"/>).
/// </summary>
internal static partial class BuildOperation
{
    private const int MaxShown = 20;

    /// <summary>At most this many warnings <c>--warnings</c> lists; the rest are only in the results file.</summary>
    internal const int MaxWarningsShown = 20;

    /// <summary>A build that takes at least this long says where its time went.</summary>
    internal const double TimesFromSeconds = 10;

    private static readonly string[] QuietArguments = ["-nologo", "-tl:off", "-v:q", "-clp:ErrorsOnly;NoSummary"];

    /// <summary>The line printed above the answer when a source file moved during the build and it ran again.</summary>
    internal const string RanAgainLine = "fuse: a source file moved while the build ran, so it ran again";

    public static Task<OperationResult> RunAsync(RepoRoot? root, string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        RunAsync(root, workingDirectory, arguments, BuildOptions.Default, cancellationToken);

    /// <param name="options">How many errors to print, in what form, and whether the warnings are listed.</param>
    public static async Task<OperationResult> RunAsync(RepoRoot? root, string workingDirectory, IReadOnlyList<string> arguments, BuildOptions options, CancellationToken cancellationToken)
    {
        // The arguments are the user's, unchanged, so an implicit restore still happens: an agent that has just added a
        // package reference and then builds needs it. Fuse adds the lock and leaves the command as it is.
        if (root is null)
            return await BuildAsync(workingDirectory, workingDirectory, Path.GetTempPath(), arguments, options, null, cancellationToken).ConfigureAwait(false);

        using var buildLock = BuildLock.AcquireForClient(root);
        return await BuildAsync(workingDirectory, root.Path, root.StateDirectory, arguments, options, root, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OperationResult> BuildAsync(string workingDirectory, string root, string logDirectory, IReadOnlyList<string> arguments, BuildOptions options, RepoRoot? resultsRoot, CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        var (result, log) = await RunLoggedAsync(["build", .. arguments, .. QuietArguments], workingDirectory, logDirectory, cancellationToken).ConfigureAwait(false);
        // A source file that another agent moved while MSBuild compiled is missing from the file list it evaluated.
        // Evaluated again, the list is right, so the build runs once more.
        if (result.ExitCode != 0 && MovedSources(result.Output).Any(path => !File.Exists(path)))
        {
            (result, log) = await RunLoggedAsync(["build", .. arguments, .. QuietArguments], workingDirectory, logDirectory, cancellationToken).ConfigureAwait(false);
            var again = Finish(result, log);
            return again with { Text = RanAgainLine + "\n" + again.Text };
        }

        return Finish(result, log);

        OperationResult Finish(ProcessResult finished, string finishedLog)
        {
            var diagnostics = BuildOutputParser.Diagnostics(finished.Output, root);
            var warnings = finishedLog.Length == 0 ? [] : BuildLog.WarningsList(finishedLog, root);
            var resultsPath = resultsRoot is null ? null : BuildResultsFile.WriteBuild(resultsRoot, diagnostics, warnings, options.Json);
            return Render(finished, root, (Environment.TickCount64 - started) / 1000.0, "build", options, resultsPath, finishedLog);
        }
    }

    /// <summary>
    ///     Runs <c>dotnet</c> with <paramref name="arguments"/> and the file log, and returns the process and the log's text,
    ///     which is empty when MSBuild wrote none, as for a command line it refused.
    /// </summary>
    /// <param name="logDirectory">Where the log is written; it is deleted once read.</param>
    internal static async Task<(ProcessResult Result, string Log)> RunLoggedAsync(string[] arguments, string workingDirectory, string logDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(logDirectory, $"build-{Guid.NewGuid():N}.log");
        try
        {
            try
            {
                Directory.CreateDirectory(logDirectory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Without a place for the log, the build runs as it did before the log existed.
                return (await ProcessRunner.RunAsync("dotnet", arguments, workingDirectory, cancellationToken).ConfigureAwait(false), "");
            }

            var result = await ProcessRunner.RunAsync("dotnet", [.. arguments, .. BuildLog.Arguments(path)], workingDirectory, cancellationToken).ConfigureAwait(false);
            return (result, ReadLog(path));
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string ReadLog(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>The source files CS2001 says could not be found, as absolute paths.</summary>
    internal static IEnumerable<string> MovedSources(string output) =>
        MissingSource().Matches(output).Select(m => m.Groups["path"].Value).Where(Path.IsPathRooted);

    /// <param name="log">The build's file log (<see cref="BuildLog"/>), or empty when it has none.</param>
    internal static OperationResult Render(ProcessResult result, string root, double seconds, string operation, string log = "") =>
        Render(result, root, seconds, operation, new BuildOptions(MaxShown, false, false), null, log);

    /// <param name="options">How many errors to print, in what form, and whether the warnings are listed.</param>
    /// <param name="resultsPath">Where every error and warning was written, or null when it was not written.</param>
    /// <param name="log">The build's file log (<see cref="BuildLog"/>), or empty when it has none.</param>
    internal static OperationResult Render(ProcessResult result, string root, double seconds, string operation, BuildOptions options, string? resultsPath = null, string log = "")
    {
        var diagnostics = BuildOutputParser.Diagnostics(result.Output, root);
        var commands = log.Length == 0 ? [] : BuildLog.FailedCommands(log);
        var warnings = options.ListWarnings && log.Length > 0 ? ListedWarnings(BuildLog.WarningsList(log, root), diagnostics) : [];
        var text = new StringBuilder();
        if (options.Json)
        {
            foreach (var diagnostic in diagnostics)
                text.Append(DiagnosticJson.BuildLine(diagnostic)).Append('\n');
        }
        else if (diagnostics.Count > 0 || result.ExitCode == 0)
        {
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
            }
        }
        else
        {
            // Nothing matched the diagnostic format: show the tail, which is where MSBuild puts the failure.
            text.Append(Tail(result.Output)).Append('\n');
        }

        if (options.ListWarnings)
            AppendWarnings(text, warnings, resultsPath);

        var times = seconds >= TimesFromSeconds && log.Length > 0 ? BuildLog.Times(log) : null;
        var timesTail = times is null ? "" : $" ({times})";
        var warningsCount = log.Length == 0 ? 0 : BuildLog.Warnings(log);
        var warningsTail = warningsCount > 0 ? $"; {warningsCount} warning(s)" : "";
        if (result.ExitCode == 0)
        {
            text.Append($"fuse: {operation} succeeded in {seconds:0.0} s{warningsTail}{timesTail}");
            return new OperationResult(Outcome.Clean, text.ToString());
        }

        if (diagnostics.Count == 0)
        {
            text.Append($"fuse: {operation} failed (exit code {result.ExitCode}) in {seconds:0.0} s{warningsTail}{timesTail}");
            return new OperationResult(Outcome.ProblemsFound, text.ToString());
        }

        var shown = options.Json ? diagnostics.Count : Math.Min(options.MaxShown, diagnostics.Count);
        var more = !options.Json && diagnostics.Count > shown ? $", first {shown} shown" : "";
        // The file holds every error; the answer names it only when it left errors out, and never in JSON, which already holds them.
        var file = !options.Json && diagnostics.Count > shown && resultsPath is not null ? $"; all {diagnostics.Count} in {resultsPath}" : "";
        text.Append($"fuse: {operation} failed with {diagnostics.Count} error(s){more} in {seconds:0.0} s{warningsTail}{timesTail}{file}");
        return new OperationResult(Outcome.ProblemsFound, text.ToString());
    }

    /// <summary>The warnings that are not also errors: one treated as an error already prints as one.</summary>
    private static List<BuildWarning> ListedWarnings(IReadOnlyList<BuildWarning> warnings, IReadOnlyList<BuildDiagnostic> diagnostics)
    {
        var errors = diagnostics.Select(d => (d.File, d.Line, d.Column, d.Code)).ToHashSet();
        return [.. warnings.Where(w => !errors.Contains((w.File, (int?)w.Line, (int?)w.Column, w.Code)))];
    }

    /// <summary>The warnings after the errors, each indented, with where the rest are when they do not all fit.</summary>
    private static void AppendWarnings(StringBuilder text, List<BuildWarning> warnings, string? resultsPath)
    {
        foreach (var warning in warnings.Take(MaxWarningsShown))
            text.Append("  ").Append(warning).Append('\n');
        if (warnings.Count > MaxWarningsShown)
            text.Append("  ... ").Append(warnings.Count - MaxWarningsShown).Append(" more warning(s)").Append(resultsPath is null ? " not shown" : $" in {resultsPath}").Append('\n');
    }

    /// <summary>The last 30 non-empty lines of a process's output, which is where a failure without an error line is described.</summary>
    internal static string Tail(string output) =>
        string.Join('\n', output.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(30));

    [GeneratedRegex(@"error CS2001: Source file '(?<path>[^']+)' could not be found")]
    private static partial Regex MissingSource();
}
