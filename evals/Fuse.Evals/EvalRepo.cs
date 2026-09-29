using System.Diagnostics;
using System.Text.RegularExpressions;
using Fuse.Dotnet;
using Fuse.Paths;

namespace Fuse.Evals;

/// <summary>A git repository under evaluation: how to build it, how to run fuse in it, and how to put it back to HEAD.</summary>
internal sealed partial class EvalRepo
{
    public EvalRepo(string root, string fuse, string buildTarget)
    {
        Root = Path.GetFullPath(root);
        Fuse = fuse;
        BuildTarget = buildTarget;
    }

    public string Root { get; }

    /// <summary>Path of the fuse executable under test (the product entry point).</summary>
    public string Fuse { get; }

    /// <summary>Solution or project the truth build compiles.</summary>
    public string BuildTarget { get; }

    public string Name => Path.GetFileName(Root);

    public async Task<ProcessResult> GitAsync(params string[] args) =>
        await ProcessRunner.RunAsync("git", ["-c", "core.quotepath=off", .. args], Root, CancellationToken.None);

    /// <summary>Discards every working-tree change, including new untracked files, keeping ignored build output.</summary>
    public async Task ResetAsync()
    {
        await GitAsync("checkout", "--", ".");
        await GitAsync("clean", "-fdq");
    }

    public async Task<bool> IsCleanAsync() => (await GitAsync("status", "--porcelain")).Output.Trim().Length == 0;

    /// <summary>The commit the repository is measured at, so a result file names the tree behind its numbers.</summary>
    public async Task<string> HeadAsync()
    {
        var result = await GitAsync("rev-parse", "HEAD");
        return result.ExitCode == 0 ? result.Output.Trim() : "";
    }

    /// <summary>The product version the fuse executable under test reports for itself.</summary>
    /// <summary>
    ///     The build under test, in the form the engine compares (<c>version/module id</c>), so two result files from two
    ///     builds of one version can be told apart. Falls back to the printed version when the assembly cannot be read.
    /// </summary>
    public async Task<string> VersionAsync()
    {
        var version = (await FuseAsync("--version")).Result.Output.Trim();
        var assembly = Path.ChangeExtension(Fuse, ".dll");
        try
        {
            using var stream = File.OpenRead(assembly);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            return $"{version}/{metadata.GetGuid(metadata.GetModuleDefinition().Mvid):N}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return version;
        }
    }

    /// <summary>
    ///     The engine log this repository's engine writes, read as lines with the leading timestamp stripped, so a caller
    ///     can match them against the formats in <see cref="Fuse.Telemetry.PhaseLine"/>. The engine is the only writer and
    ///     it is idle between requests, so a read cannot catch a line half-written.
    /// </summary>
    public IReadOnlyList<string> EngineLogLines()
    {
        var path = Path.Combine(StateDirectory, "engine.log");
        if (!File.Exists(path))
            return [];

        return [.. File.ReadAllLines(path).Select(line => Timestamp().Replace(line, "", 1).Trim()).Where(line => line.Length > 0)];
    }

    /// <summary>Where this repository's engine keeps its state, which is where its log is.</summary>
    public string StateDirectory => RepoRoot.Find(Root)?.StateDirectory ?? Path.Combine(Root, "fuse-state");

    /// <summary>
    ///     Runs <c>dotnet build</c> on the truth target and returns its error lines (relative paths, canonical form).
    ///     A warm engine must not hold a file this build writes; if the build fails to copy one (MSB3021, MSB3027), that is
    ///     a product defect a user would hit, so the suite stops and says so rather than measuring a broken truth build.
    /// </summary>
    public async Task<(List<string> Errors, int ExitCode, double Seconds)> BuildAsync()
    {
        var (errors, exitCode, seconds) = await BuildOnceAsync();
        ThrowIfLocked(errors, "the truth build");
        return (errors, exitCode, seconds);
    }

    /// <summary>Stops the suite when <paramref name="errors"/> show a build could not replace a file something held open.</summary>
    internal static void ThrowIfLocked(IEnumerable<string> errors, string what)
    {
        var locked = errors.FirstOrDefault(e => e.Contains("MSB3021", StringComparison.Ordinal) || e.Contains("MSB3027", StringComparison.Ordinal));
        if (locked is not null)
            throw new InvalidOperationException($"{what} could not replace a file that another process holds open, which is a defect to fix, not a result: {locked}");
    }

    private async Task<(List<string> Errors, int ExitCode, double Seconds)> BuildOnceAsync()
    {
        var watch = Stopwatch.StartNew();
        var result = await ProcessRunner.RunAsync(
            "dotnet",
            ["build", BuildTarget, "--no-restore", "-nologo", "-tl:off", "-v:q", "-clp:ErrorsOnly;NoSummary"],
            Root,
            CancellationToken.None);
        return (BuildOutputParser.Errors(result.Output, Root), result.ExitCode, watch.Elapsed.TotalSeconds);
    }

    /// <summary>Runs <c>fuse</c> with arguments in the repository and returns its output and wall time.</summary>
    public async Task<(ProcessResult Result, double Milliseconds)> FuseAsync(params string[] args)
    {
        var watch = Stopwatch.StartNew();
        var result = await ProcessRunner.RunAsync(Fuse, args, Root, CancellationToken.None);
        return (result, watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>One timed call: its output, its wall time, and the engine request it made.</summary>
    public async Task<FuseRun> FuseTimedAsync(params string[] args)
    {
        var (result, milliseconds) = await FuseAsync(args);
        return new FuseRun(result, milliseconds);
    }

    /// <summary>Stops every fuse engine serving this repository so the next call starts cold.</summary>
    public async Task KillEngineAsync()
    {
        foreach (var process in await EngineProcessesAsync())
        {
            try
            {
                Process.GetProcessById(process.Pid).Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        await Task.Delay(500);
    }

    /// <summary>The fuse engine processes whose command line names this repository, with their working set in bytes.</summary>
    public async Task<List<(int Pid, long WorkingSet)>> EngineProcessesAsync()
    {
        var result = new List<(int, long)>();
        if (OperatingSystem.IsWindows())
        {
            var query = await ProcessRunner.RunAsync(
                "powershell",
                ["-NoProfile", "-Command", "Get-CimInstance Win32_Process -Filter \"Name='fuse.exe'\" | ForEach-Object { \"$($_.ProcessId)`t$($_.WorkingSetSize)`t$($_.CommandLine)\" }"],
                Root,
                CancellationToken.None);
            foreach (var line in query.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length == 3 && parts[2].Contains(" engine ", StringComparison.Ordinal) && parts[2].Contains(Root, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(parts[0], out var pid) && long.TryParse(parts[1], out var ws))
                    result.Add((pid, ws));
            }
        }
        else
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid))
                    continue;
                try
                {
                    var cmd = File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\0', ' ');
                    if (cmd.Contains(" engine ", StringComparison.Ordinal) && cmd.Contains(Root, StringComparison.Ordinal))
                        result.Add((pid, Process.GetProcessById(pid).WorkingSet64));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                {
                }
            }
        }

        return result;
    }

    /// <summary>Error lines from fuse check output (canonical form).</summary>
    public static List<string> ParseFuseErrors(string output) =>
        [.. output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => FuseError().IsMatch(l))];

    /// <summary>The count in the "fuse: N error(s) introduced" summary line, or 0 when there is none.</summary>
    public static int ParseFuseCount(string output)
    {
        var match = FuseCount().Match(output);
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    [GeneratedRegex(@"^[^\r\n]+\(\d+,\d+\): error [A-Za-z]+\d+: .*$")]
    private static partial Regex FuseError();
    [GeneratedRegex(@"fuse: (\d+) error\(s\) introduced")]
    private static partial Regex FuseCount();

    // The engine prefixes every line with "yyyy-MM-dd HH:mm:ss.fff ", two fields separated by a space.
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+ ")]
    private static partial Regex Timestamp();
}

/// <summary>One run of the fuse executable: what it printed, how long the client waited, and its exit code.</summary>
internal sealed record FuseRun(ProcessResult Result, double Milliseconds)
{
    public int ExitCode => Result.ExitCode;

    public string Output => Result.Output;
}
