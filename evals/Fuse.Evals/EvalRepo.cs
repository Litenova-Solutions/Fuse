using System.Diagnostics;
using System.Text.RegularExpressions;
using Fuse.Dotnet;

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
    public async Task<string> VersionAsync() => (await FuseAsync("--version")).Result.Output.Trim();

    /// <summary>
    ///     Runs <c>dotnet build</c> on the truth target and returns its error lines (relative paths, canonical form).
    ///     A warm engine holds the source generators and analyzers it loaded mapped for its lifetime, so the build's copy
    ///     of one of those can fail with MSB3021 or MSB3027. That is a real defect in the product, recorded in
    ///     <c>decisions.md</c>, but it is not a compiler error, and counting it as one would leave the truth side of the
    ///     suite a broken build. The engine is killed and the build repeated once, so what is measured is a build a user
    ///     could have got.
    /// </summary>
    public async Task<(List<string> Errors, int ExitCode, double Seconds)> BuildAsync()
    {
        var (errors, exitCode, seconds) = await BuildOnceAsync();
        if (exitCode != 0 && errors.Any(e => e.Contains("MSB3021", StringComparison.Ordinal) || e.Contains("MSB3027", StringComparison.Ordinal)))
        {
            Console.WriteLine("[build] the build could not copy a file a warm engine holds open; killing the engine and building again");
            await KillEngineAsync();
            (errors, exitCode, seconds) = await BuildOnceAsync();
        }

        return (errors, exitCode, seconds);
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

    /// <summary>Stops every fuse engine serving this repository so the next call starts cold.</summary>
    public async Task KillEngineAsync()
    {
        foreach (var process in await EngineProcessesAsync())
        {
            try
            {
                Process.GetProcessById(process.Pid).Kill();
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

    /// <summary>The count in the "fuse: N new error(s)" summary line, or 0 when there is none.</summary>
    public static int ParseFuseCount(string output)
    {
        var match = FuseCount().Match(output);
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    [GeneratedRegex(@"^[^\r\n]+\(\d+,\d+\): error [A-Za-z]+\d+: .*$")]
    private static partial Regex FuseError();

    [GeneratedRegex(@"fuse: (\d+) new error")]
    private static partial Regex FuseCount();
}
