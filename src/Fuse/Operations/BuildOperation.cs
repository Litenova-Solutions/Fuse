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

    /// <summary>A build that takes at least this long says where its time went.</summary>
    internal const double TimesFromSeconds = 10;

    private static readonly string[] QuietArguments = ["-nologo", "-tl:off", "-v:q", "-clp:ErrorsOnly;NoSummary"];

    /// <summary>The line printed above the answer when a source file moved during the build and it ran again.</summary>
    internal const string RanAgainLine = "fuse: a source file moved while the build ran, so it ran again";

    public static async Task<OperationResult> RunAsync(RepoRoot? root, string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        // The arguments are the user's, unchanged, so an implicit restore still happens: an agent that has just added a
        // package reference and then builds needs it. Fuse adds the lock and leaves the command as it is.
        if (root is null)
            return await BuildAsync(workingDirectory, workingDirectory, Path.GetTempPath(), arguments, cancellationToken).ConfigureAwait(false);

        using var buildLock = BuildLock.AcquireForClient(root);
        return await BuildAsync(workingDirectory, root.Path, root.StateDirectory, arguments, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OperationResult> BuildAsync(string workingDirectory, string root, string logDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        var (result, log) = await RunLoggedAsync(["build", .. arguments, .. QuietArguments], workingDirectory, logDirectory, cancellationToken).ConfigureAwait(false);
        // A source file that another agent moved while MSBuild compiled is missing from the file list it evaluated.
        // Evaluated again, the list is right, so the build runs once more.
        if (result.ExitCode != 0 && MovedSources(result.Output).Any(path => !File.Exists(path)))
        {
            (result, log) = await RunLoggedAsync(["build", .. arguments, .. QuietArguments], workingDirectory, logDirectory, cancellationToken).ConfigureAwait(false);
            var again = Render(result, root, (Environment.TickCount64 - started) / 1000.0, "build", log);
            return again with { Text = RanAgainLine + "\n" + again.Text };
        }

        return Render(result, root, (Environment.TickCount64 - started) / 1000.0, "build", log);
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
    internal static OperationResult Render(ProcessResult result, string root, double seconds, string operation, string log = "")
    {
        var errors = BuildOutputParser.Errors(result.Output, root);
        var commands = log.Length == 0 ? [] : BuildLog.FailedCommands(log);
        var text = new StringBuilder();
        foreach (var error in errors.Take(MaxShown))
        {
            text.Append(error).Append('\n');
            // What a failed command printed explains its exit code, which is all the error line says.
            foreach (var (command, lines) in commands)
            {
                if (!error.Contains($"error MSB3073: The command \"{command}", StringComparison.Ordinal))
                    continue;
                foreach (var line in lines)
                    text.Append("  ").Append(line).Append('\n');
                break;
            }
        }

        var times = seconds >= TimesFromSeconds && log.Length > 0 ? BuildLog.Times(log) : null;
        var timesTail = times is null ? "" : $" ({times})";
        if (result.ExitCode == 0)
        {
            text.Append($"fuse: {operation} succeeded in {seconds:0.0} s{timesTail}");
            return new OperationResult(Outcome.Clean, text.ToString());
        }

        if (errors.Count == 0)
        {
            // Nothing matched the diagnostic format: show the tail, which is where MSBuild puts the failure.
            text.Append(Tail(result.Output)).Append('\n');
            text.Append($"fuse: {operation} failed (exit code {result.ExitCode}) in {seconds:0.0} s{timesTail}");
            return new OperationResult(Outcome.ProblemsFound, text.ToString());
        }

        var more = errors.Count > MaxShown ? $", first {MaxShown} shown" : "";
        text.Append($"fuse: {operation} failed with {errors.Count} error(s){more} in {seconds:0.0} s{timesTail}");
        return new OperationResult(Outcome.ProblemsFound, text.ToString());
    }

    /// <summary>The last 30 non-empty lines of a process's output, which is where a failure without an error line is described.</summary>
    internal static string Tail(string output) =>
        string.Join('\n', output.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(30));

    [GeneratedRegex(@"error CS2001: Source file '(?<path>[^']+)' could not be found")]
    private static partial Regex MissingSource();
}
