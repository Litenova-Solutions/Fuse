using System.Text;
using System.Text.RegularExpressions;
using Fuse.Dotnet;
using Fuse.Engine.Client;
using Fuse.Failures;
using Fuse.Paths;
using Fuse.Protocol;
using Fuse.Repo;

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

    /// <summary>At most this many lock files the refresh line names; the rest are only counted.</summary>
    internal const int MaxLockFilesShown = 10;

    public static Task<OperationResult> RunAsync(RepoRoot? root, string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        RunAsync(root, workingDirectory, arguments, BuildOptions.Default, cancellationToken);

    /// <param name="options">How many errors to print, in what form, and whether the warnings are listed.</param>
    /// <param name="includeTreeStamp">True to stamp the summary and the results file with the tree the build ended on, for <c>fuse build</c>; the MCP answer and the test build stay unstamped.</param>
    public static async Task<OperationResult> RunAsync(RepoRoot? root, string workingDirectory, IReadOnlyList<string> arguments, BuildOptions options, CancellationToken cancellationToken, bool includeTreeStamp = false)
    {
        // The arguments are the user's, unchanged, so an implicit restore still happens: an agent that has just added a
        // package reference and then builds needs it. Fuse adds the lock and leaves the command as it is.
        if (root is null)
            return await BuildAsync(workingDirectory, workingDirectory, Path.GetTempPath(), arguments, options, null, false, cancellationToken).ConfigureAwait(false);

        using var buildLock = BuildLock.AcquireForClient(root);
        return await BuildAsync(workingDirectory, root.Path, root.StateDirectory, arguments, options, root, includeTreeStamp, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Builds the projects owning the files changed since HEAD and their dependents, one <c>dotnet build</c> per
    ///     highest project under one lock, each with <paramref name="arguments"/>. When nothing changed it builds
    ///     nothing; when the selection cannot be trusted it builds the whole solution instead.
    /// </summary>
    /// <param name="root">The repository to build the changed projects of.</param>
    /// <param name="workingDirectory">Where <c>dotnet build</c> runs.</param>
    /// <param name="arguments">The arguments for each <c>dotnet build</c>, unchanged.</param>
    /// <param name="options">How many errors to print, in what form, and whether the warnings are listed.</param>
    /// <param name="cancellationToken">Cancels the plan and the builds.</param>
    public static async Task<OperationResult> RunChangedAsync(RepoRoot root, string workingDirectory, IReadOnlyList<string> arguments, BuildOptions options, CancellationToken cancellationToken)
    {
        var response = await EngineClient.SendAsync(root, new EngineRequest.PlanChangedBuild(), TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        if (response is not EngineResponse.ChangedBuildAnswered { Plan: not null } answered)
            return OperationResult.Unanswered(response, "changed build plan");
        var plan = answered.Plan;
        if (plan.FallbackReason.Length > 0)
            return await FallbackAsync(root, workingDirectory, arguments, options, plan.FallbackReason, cancellationToken).ConfigureAwait(false);
        if (plan.BuildProjects.Length == 0)
        {
            if (plan.ChangedFileCount == 0)
                return new OperationResult(Outcome.Clean, $"fuse: no C# changes since HEAD; nothing to build (tree {plan.TreeHash})");
            return await FallbackAsync(root, workingDirectory, arguments, options, $"{plan.ChangedFileCount} changed file(s) in no project", cancellationToken).ConfigureAwait(false);
        }

        var names = plan.BuildProjects.Select(project => Path.GetFileNameWithoutExtension(project) ?? project).ToArray();
        var text = new StringBuilder();
        text.Append($"fuse: building {plan.BuildProjects.Length} project(s) for {plan.ChangedFileCount} changed file(s) (tree {plan.TreeHash}): {string.Join(", ", names)}");
        if (!new HashSet<string>(names).SetEquals(plan.ChangedInProjects))
            text.Append($"; changes in {string.Join(", ", plan.ChangedInProjects)}");
        if (plan.UnownedFileCount > 0)
            text.Append($"; {plan.UnownedFileCount} changed file(s) in no project");
        using var buildLock = BuildLock.AcquireForClient(root);
        var stamp = await BuildResultsFile.StampForReuseAsync(root, cancellationToken).ConfigureAwait(false);
        if (stamp is not null && ReusedBuild(root, stamp.Value, BuildResultsFile.ChangedScope, plan.BuildProjects, arguments, workingDirectory, options) is { } reused)
            return reused;
        var runs = new List<(ProcessResult Result, string Log, double Seconds, bool RanAgain)>(plan.BuildProjects.Length);
        foreach (var project in plan.BuildProjects)
            runs.Add(await RunOneAsync(["build", project, .. arguments, .. QuietArguments], workingDirectory, root.StateDirectory, cancellationToken).ConfigureAwait(false));

        // The results file holds every project at once; each summary names it with the combined count.
        var treeStamp = await StampAsync(root, cancellationToken).ConfigureAwait(false);
        var parsed = runs.Select((run, i) => BuildRun($"build ({names[i]})", run.Result, run.Log, run.Seconds, BuildOutputParser.Diagnostics(run.Result.Output, root.Path), run.Log.Length == 0 ? [] : BuildLog.WarningsList(run.Log, root.Path), RelativeProject(plan.BuildProjects[i], root.Path))).ToList();
        var errors = parsed.SelectMany(run => run.Errors).ToList();
        var warnings = parsed.SelectMany(run => run.Warnings).ToList();

        var resultsPath = BuildResultsFile.WriteBuild(root, errors, warnings, options.Json, treeStamp);
        if (stamp is not null && ShouldRecord(errors))
            BuildResultsFile.WriteReuse(root, stamp.Value, BuildResultsFile.ChangedScope, plan.BuildProjects, arguments, RelativeWorkingDirectory(root, workingDirectory), DateTime.UtcNow, parsed, treeStamp, errors.Count);
        var failed = 0;
        foreach (var ((result, log, seconds, ranAgain), name, project) in runs.Zip(names, plan.BuildProjects))
        {
            text.Append('\n');
            if (ranAgain)
                text.Append(RanAgainLine).Append('\n');
            var rendered = Render(result, root.Path, seconds, $"build ({name})", options, resultsPath, log, treeStamp, errors.Count, restoreTarget: RelativeProject(project, root.Path));
            if (rendered.Outcome == Outcome.ProblemsFound)
                failed++;
            text.Append(rendered.Text);
        }

        if (plan.BuildProjects.Length > 1)
            text.Append($"\nfuse: {failed} of {plan.BuildProjects.Length} project(s) failed");
        return new OperationResult(failed > 0 ? Outcome.ProblemsFound : Outcome.Clean, text.ToString());
    }

    /// <summary>Builds the whole solution because <paramref name="reason"/> says the changed projects cannot be selected, saying so first.</summary>
    private static async Task<OperationResult> FallbackAsync(RepoRoot root, string workingDirectory, IReadOnlyList<string> arguments, BuildOptions options, string reason, CancellationToken cancellationToken)
    {
        var whole = await RunAsync(root, workingDirectory, arguments, options, cancellationToken, includeTreeStamp: true).ConfigureAwait(false);
        return whole with { Text = $"fuse: {reason}; building the whole solution instead\n" + whole.Text };
    }

    /// <summary>The stamp for a finished build, or null when git cannot say what the tree holds, which leaves the build unstamped rather than failing it.</summary>
    private static async Task<string?> StampAsync(RepoRoot root, CancellationToken cancellationToken)
    {
        try
        {
            var (hash, count) = await TreeStamp.ForWorkingTreeAsync(root, cancellationToken).ConfigureAwait(false);
            return TreeStamp.SummaryPart(hash, count);
        }
        catch (FuseException)
        {
            return null;
        }
    }

    private static async Task<OperationResult> BuildAsync(string workingDirectory, string root, string logDirectory, IReadOnlyList<string> arguments, BuildOptions options, RepoRoot? resultsRoot, bool includeTreeStamp, CancellationToken cancellationToken)
    {
        var stamp = resultsRoot is null ? null : await BuildResultsFile.StampForReuseAsync(resultsRoot, cancellationToken).ConfigureAwait(false);
        if (resultsRoot is not null && stamp is not null && ReusedBuild(resultsRoot, stamp.Value, BuildResultsFile.WholeScope, [], arguments, workingDirectory, options) is { } reused)
            return reused;
        var (result, log, seconds, ranAgain) = await RunOneAsync(["build", .. arguments, .. QuietArguments], workingDirectory, logDirectory, cancellationToken).ConfigureAwait(false);
        var treeStamp = includeTreeStamp && resultsRoot is not null ? await StampAsync(resultsRoot, cancellationToken).ConfigureAwait(false) : null;
        var diagnostics = BuildOutputParser.Diagnostics(result.Output, root);
        var warnings = log.Length == 0 ? [] : BuildLog.WarningsList(log, root);
        var resultsPath = resultsRoot is null ? null : BuildResultsFile.WriteBuild(resultsRoot, diagnostics, warnings, options.Json, treeStamp);
        var restoreTarget = resultsRoot is null ? null : RestoreTargetHint(arguments, workingDirectory, root);
        if (stamp is not null && resultsRoot is not null && ShouldRecord(diagnostics))
            BuildResultsFile.WriteReuse(resultsRoot, stamp.Value, BuildResultsFile.WholeScope, [], arguments, RelativeWorkingDirectory(resultsRoot, workingDirectory), DateTime.UtcNow, [BuildRun("build", result, log, seconds, diagnostics, warnings, restoreTarget)], treeStamp, null);
        var rendered = Render(result, root, seconds, "build", options, resultsPath, log, treeStamp, restoreTarget: restoreTarget);
        return ranAgain ? rendered with { Text = RanAgainLine + "\n" + rendered.Text } : rendered;
    }

    /// <summary>
    ///     The last finished build's answer when it built the same tree, project set and arguments from the same
    ///     directory, or null. The answer prints the stored result with a line above each build saying it was reused
    ///     and how old it is, and the results file holds the stored errors and warnings again.
    /// </summary>
    /// <param name="scope">The whole solution, or the changed projects.</param>
    /// <param name="projects">The absolute project files a changed build builds, or none for the whole solution.</param>
    internal static OperationResult? ReusedBuild(RepoRoot root, (string TreeHash, string FullHash, string AssetsHash, int SourceCount) stamp, string scope, IReadOnlyList<string> projects, IReadOnlyList<string> arguments, string workingDirectory, BuildOptions options)
    {
        var stored = BuildResultsFile.ReadReuse(root);
        if (stored is null
            || stored.Value.TreeHash != stamp.TreeHash
            || stored.Value.FullHash != stamp.FullHash
            || stored.Value.AssetsHash != stamp.AssetsHash
            || stored.Value.Scope != scope
            || !stored.Value.Projects.SequenceEqual(projects)
            || !stored.Value.Arguments.SequenceEqual(arguments)
            || stored.Value.WorkingDirectory != RelativeWorkingDirectory(root, workingDirectory)
            || stored.Value.Runs.Count == 0)
            return null;
        var record = stored.Value;
        var resultsPath = BuildResultsFile.WriteBuild(root, [.. record.Runs.SelectMany(run => run.Errors)], [.. record.Runs.SelectMany(run => run.Warnings)], options.Json, record.TreeStamp);
        var text = new StringBuilder();
        var failed = 0;
        foreach (var run in record.Runs)
        {
            text.Append(ReusedLine(run.Label, record.FinishedUtc, record.TreeHash)).Append('\n');
            var rendered = RenderCore(root.Path, run.Errors, run.Commands, options.ListWarnings ? ListedWarnings(run.Warnings, run.Errors) : [], run.WarningsCount, run.Times, run.ExitCode, run.Tail, run.Seconds, run.Label, options, resultsPath, record.TreeStamp, record.FileTotal, run.RestoreTarget);
            if (rendered.Outcome == Outcome.ProblemsFound)
                failed++;
            text.Append(rendered.Text).Append('\n');
        }

        if (scope == BuildResultsFile.ChangedScope && record.Runs.Count > 1)
            text.Append($"fuse: {failed} of {record.Runs.Count} project(s) failed");
        else
            text.Length -= 1;
        return new OperationResult(failed > 0 ? Outcome.ProblemsFound : Outcome.Clean, text.ToString());
    }

    /// <summary>True when the finished build is worth reusing: a package content hash failure reads inputs outside the tree, so the next build runs instead.</summary>
    internal static bool ShouldRecord(IReadOnlyList<BuildDiagnostic> diagnostics) =>
        diagnostics.All(diagnostic => !diagnostic.IsContentHashFailure);

    /// <summary>One finished build as the reuse record holds it.</summary>
    private static (string Label, int ExitCode, double Seconds, IReadOnlyList<BuildDiagnostic> Errors, IReadOnlyList<BuildWarning> Warnings, int WarningsCount, IReadOnlyList<(string Command, List<string> Lines)> Commands, string? Times, string Tail, string? RestoreTarget) BuildRun(string label, ProcessResult result, string log, double seconds, IReadOnlyList<BuildDiagnostic> errors, IReadOnlyList<BuildWarning> warnings, string? restoreTarget) =>
        (label, result.ExitCode, seconds, errors, warnings, log.Length == 0 ? 0 : BuildLog.Warnings(log), log.Length == 0 ? [] : BuildLog.FailedCommands(log), seconds >= TimesFromSeconds && log.Length > 0 ? BuildLog.Times(log) : null, Tail(result.Output), restoreTarget);

    /// <summary>The line above a reused answer: what was reused and how old it is.</summary>
    private static string ReusedLine(string label, DateTime finishedUtc, string treeHash) =>
        $"fuse: {label} reused from {BuildResultsFile.AgeAgo(finishedUtc, DateTime.UtcNow)} ago; tree {treeHash}";

    /// <summary><paramref name="workingDirectory"/> as the reuse key holds it: relative to the root, or as given when it is not under it.</summary>
    private static string RelativeWorkingDirectory(RepoRoot root, string workingDirectory)
    {
        try
        {
            var relative = Path.GetRelativePath(root.Path, workingDirectory).Replace('\\', '/');
            return relative.StartsWith("..", StringComparison.Ordinal) ? workingDirectory : relative;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            return workingDirectory;
        }
    }

    /// <summary><paramref name="project"/> as an answer prints it: relative to <paramref name="root"/>.</summary>
    private static string RelativeProject(string project, string root)
    {
        try
        {
            return Path.GetRelativePath(root, project).Replace('\\', '/');
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            return project.Replace('\\', '/');
        }
    }

    /// <summary>What the user's build arguments already name to restore: the first project or solution among them, relative to the root, or null.</summary>
    internal static string? RestoreTargetHint(IReadOnlyList<string> arguments, string workingDirectory, string root)
    {
        foreach (var token in arguments)
        {
            if (token.StartsWith('-'))
                continue;
            if (!token.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                && !token.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                && !token.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                return Path.GetRelativePath(root, Path.GetFullPath(token, workingDirectory)).Replace('\\', '/');
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
            {
                return token.Replace('\\', '/');
            }
        }

        return null;
    }

    /// <summary>
    ///     The line under a package content hash failure: the one restore that refreshes the lock files and the files it
    ///     refreshes. The caller runs nothing; the user runs the command.
    /// </summary>
    /// <param name="root">The repository the lock files are listed relative to.</param>
    /// <param name="restoreTarget">What to restore, relative to the root, or null to derive it from the repository.</param>
    public static string LockRefreshLine(string root, string? restoreTarget)
    {
        var target = restoreTarget ?? DiscoveredSolution(root);
        var command = target is null ? "dotnet restore --force-evaluate" : $"dotnet restore {target} --force-evaluate";
        var locks = LockFiles(root);
        if (locks.Count == 0)
            return $"lock file: run {command} to refresh the restore";
        var shown = string.Join(", ", locks.Take(MaxLockFilesShown));
        var more = locks.Count > MaxLockFilesShown ? $" and {locks.Count - MaxLockFilesShown} more" : "";
        return $"lock file: run {command} to refresh {shown}{more}";
    }

    /// <summary>The repository's solution when it holds exactly one, relative to the root, or null.</summary>
    private static string? DiscoveredSolution(string root)
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
    private static List<string> LockFiles(string root)
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

    /// <summary>Runs one <c>dotnet</c> build with the file log, running again when a source file moved mid-build.</summary>
    /// <returns>The process, the log's text, the seconds it took, and whether it ran again.</returns>
    private static async Task<(ProcessResult Result, string Log, double Seconds, bool RanAgain)> RunOneAsync(string[] executableArguments, string workingDirectory, string logDirectory, CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        var (result, log) = await RunLoggedAsync(executableArguments, workingDirectory, logDirectory, cancellationToken).ConfigureAwait(false);
        // A source file that another agent moved while MSBuild compiled is missing from the file list it evaluated.
        // Evaluated again, the list is right, so the build runs once more.
        if (result.ExitCode != 0 && MovedSources(result.Output).Any(path => !File.Exists(path)))
        {
            (result, log) = await RunLoggedAsync(executableArguments, workingDirectory, logDirectory, cancellationToken).ConfigureAwait(false);
            return (result, log, (Environment.TickCount64 - started) / 1000.0, true);
        }

        return (result, log, (Environment.TickCount64 - started) / 1000.0, false);
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
    /// <param name="treeStamp">The stamp a <c>fuse build</c> summary carries, or null to print none.</param>
    /// <param name="fileTotal">How many errors the results file holds when it holds more than this build's, or null.</param>
    /// <param name="restoreTarget">What a lock refresh restores, relative to the root, or null to derive it from the repository.</param>
    internal static OperationResult Render(ProcessResult result, string root, double seconds, string operation, BuildOptions options, string? resultsPath = null, string log = "", string? treeStamp = null, int? fileTotal = null, string? restoreTarget = null)
    {
        var diagnostics = BuildOutputParser.Diagnostics(result.Output, root);
        List<(string Command, List<string> Lines)> commands = log.Length == 0 ? [] : BuildLog.FailedCommands(log);
        List<BuildWarning> warnings = options.ListWarnings && log.Length > 0 ? ListedWarnings(BuildLog.WarningsList(log, root), diagnostics) : [];
        var warningsCount = log.Length == 0 ? 0 : BuildLog.Warnings(log);
        var times = seconds >= TimesFromSeconds && log.Length > 0 ? BuildLog.Times(log) : null;
        return RenderCore(root, diagnostics, commands, warnings, warningsCount, times, result.ExitCode, Tail(result.Output), seconds, operation, options, resultsPath, treeStamp, fileTotal, restoreTarget);
    }

    /// <summary>Renders parsed diagnostics the way a fresh build prints them: the errors, the lock and refresh lines under them, the warnings, and the summary.</summary>
    /// <param name="root">The repository the errors print relative to.</param>
    /// <param name="diagnostics">The errors in first-seen order.</param>
    /// <param name="commands">Each failed command with the last lines it printed.</param>
    /// <param name="warnings">The warnings that are not also errors, listed only with <c>--warnings</c>.</param>
    /// <param name="warningsCount">How many warnings the build produced.</param>
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
    private static OperationResult RenderCore(string root, IReadOnlyList<BuildDiagnostic> diagnostics, IReadOnlyList<(string Command, List<string> Lines)> commands, List<BuildWarning> warnings, int warningsCount, string? times, int exitCode, string tail, double seconds, string operation, BuildOptions options, string? resultsPath, string? treeStamp, int? fileTotal, string? restoreTarget)
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
        var warningsTail = warningsCount > 0 ? $"; {warningsCount} warning(s)" : "";
        if (exitCode == 0)
        {
            text.Append($"fuse: {operation} succeeded in {seconds:0.0} s{warningsTail}{timesTail}{treeStamp}");
            return new OperationResult(Outcome.Clean, text.ToString());
        }

        if (diagnostics.Count == 0)
        {
            text.Append($"fuse: {operation} failed (exit code {exitCode}) in {seconds:0.0} s{warningsTail}{timesTail}{treeStamp}");
            return new OperationResult(Outcome.ProblemsFound, text.ToString());
        }

        var shown = options.Json ? diagnostics.Count : Math.Min(options.MaxShown, diagnostics.Count);
        var more = !options.Json && diagnostics.Count > shown ? $", first {shown} shown" : "";
        // The file holds every error; the answer names it only when it left errors out, and never in JSON, which already holds them.
        var file = !options.Json && diagnostics.Count > shown && resultsPath is not null ? $"; all {fileTotal ?? diagnostics.Count} in {resultsPath}" : "";
        text.Append($"fuse: {operation} failed with {diagnostics.Count} error(s){more} in {seconds:0.0} s{warningsTail}{timesTail}{treeStamp}{file}");
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
