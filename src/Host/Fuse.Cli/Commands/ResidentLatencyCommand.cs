using System.Diagnostics;
using DotMake.CommandLine;
using Fuse.Benchmarks;
using Fuse.Cli.Services;
using Fuse.Semantics;
using Fuse.Workspace;

namespace Fuse.Cli.Commands;

/// <summary>
///     Measures the resident delta-check latency (the S1 gate): builds a repository once to a binary log, holds
///     the rehydrated compilations resident, and times the speculative overlay typecheck of a real file - the
///     operation <c>fuse_check</c> invokes when a resident workspace is live. It records warm (build + rehydrate)
///     time, resident RSS, and the delta-check P50/P95 to <c>results/resident-latency.json</c>.
/// </summary>
/// <remarks>
///     This runs in its own process and never invokes MSBuildWorkspace, so it does not co-activate the two
///     Roslyn-loading closures (the fragility characterized in the S1 progress log): only the build-capture
///     rehydration closure loads here. It lives outside <c>Fuse.Benchmarks</c> deliberately, because a test
///     assembly that co-loads the resident closure with MSBuildWorkspace-based tests breaks those tests.
/// </remarks>
[CliCommand(
    Name = "resident-latency",
    Description = "Measure the resident delta-check latency (S1 gate): warm a repo's resident workspace and time the speculative overlay typecheck. Writes results/resident-latency.json.",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class ResidentLatencyCommand
{
    private const int WarmIterations = 25;

    private readonly IConsoleUI _consoleUI;

    /// <summary>Initializes a new instance of the <see cref="ResidentLatencyCommand" /> class for CLI binding.</summary>
    public ResidentLatencyCommand() : this(null!)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ResidentLatencyCommand" /> class.</summary>
    /// <param name="consoleUI">The console UI for output.</param>
    public ResidentLatencyCommand(IConsoleUI consoleUI) => _consoleUI = consoleUI;

    /// <summary>The workspace directory to measure. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory to measure. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>An optional path to write the JSON result to. Defaults to results/resident-latency.json under tests/benchmarks.</summary>
    [CliOption(Required = false, Description = "Path to write the JSON result to.")]
    public string? Output { get; set; }

    /// <summary>
    ///     Runs the resident-latency measurement.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the measurement is written.</returns>
    public async Task RunAsync(CliContext context)
    {
        var cancellationToken = context.CancellationToken;
        var root = System.IO.Path.GetFullPath(Path);
        var notes = new List<string>();

        if (!Directory.Exists(root))
        {
            _consoleUI.WriteError($"Directory not found: {root}");
            return;
        }

        var discovery = await new DotNetWorkspaceDiscoverer().DiscoverAsync(root, cancellationToken);
        var target = discovery.SolutionPath ?? discovery.ProjectPaths.FirstOrDefault();
        if (target is null)
        {
            await WriteAsync(new SuiteResult("resident-latency", Description(), null, Empty(), [], ["skipped: no solution or project to build"]), cancellationToken);
            _consoleUI.WriteError("resident-latency: no solution or project found.");
            return;
        }

        var binlog = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fuse-resident-latency-{Guid.NewGuid():N}.binlog");
        try
        {
            _consoleUI.WriteStep($"Building {target} to a binary log for resident capture");
            var warmWatch = Stopwatch.StartNew();
            if (!await BuildBinlogAsync(target, binlog, cancellationToken) || !File.Exists(binlog))
            {
                notes.Add($"skipped: build did not produce a binlog for {target}");
                await WriteAsync(new SuiteResult("resident-latency", Description(), null, Empty(), [], notes), cancellationToken);
                _consoleUI.WriteResult(string.Join("\n", notes));
                return;
            }

            using var resident = ResidentWorkspace.LoadFromBinlog(binlog, cancellationToken);
            warmWatch.Stop();
            var rssMb = Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0);

            var sample = resident.Projects
                .SelectMany(p => p.Compilation.SyntaxTrees)
                .FirstOrDefault(t => t.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    && !t.FilePath.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                    && !t.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                    && t.Length > 0);
            if (sample is null)
            {
                notes.Add($"skipped: no source file in the resident compilation (warm {warmWatch.ElapsedMilliseconds:N0} ms, {resident.Projects.Count} project(s))");
                await WriteAsync(new SuiteResult("resident-latency", Description(), null, Empty(), [], notes), cancellationToken);
                _consoleUI.WriteResult(string.Join("\n", notes));
                return;
            }

            var relativePath = sample.FilePath;
            var content = sample.ToString();

            // One untimed warm-up, then time the overlay check (the resident delta-check).
            resident.CheckOverlay(relativePath, content, cancellationToken);
            var samples = new List<double>(WarmIterations);
            for (var i = 0; i < WarmIterations; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var watch = Stopwatch.StartNew();
                resident.CheckOverlay(relativePath, content, cancellationToken);
                watch.Stop();
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }

            var p50 = PerformanceSuite.Percentile(samples, 50);
            var p95 = PerformanceSuite.Percentile(samples, 95);
            notes.Add($"repo {System.IO.Path.GetFileName(root)}, {resident.Projects.Count} resident project(s)");
            notes.Add($"resident warm (build + rehydrate): {warmWatch.ElapsedMilliseconds:N0} ms; resident RSS {rssMb:F0} MB");
            notes.Add($"resident delta-check ({WarmIterations}x) on {System.IO.Path.GetFileName(sample.FilePath)}: P50 {p50:F1} ms, P95 {p95:F1} ms");
            notes.Add($"S1 gate (delta-check P95 < 1000 ms warm): {(p95 < 1000 ? "PASS" : "FAIL")} at P95 {p95:F1} ms");
            notes.Add("note: measures ResidentWorkspace.CheckOverlay, the speculative typecheck fuse_check invokes when a resident workspace is live; timings are environment-dependent; runs in a dedicated process that never invokes MSBuildWorkspace.");

            // medianTokens carries the delta-check P50, meanTokens the warm build+rehydrate ms (mirrors PerformanceSuite).
            var scorecard = new Scorecard(1, 0, 0, 0, 0, 0, p50, warmWatch.ElapsedMilliseconds, 0);
            await WriteAsync(new SuiteResult("resident-latency", Description(), null, scorecard, [], notes), cancellationToken);
            _consoleUI.WriteResult(string.Join("\n", notes));
        }
        finally
        {
            try { if (File.Exists(binlog)) File.Delete(binlog); } catch (IOException) { }
        }
    }

    private async Task WriteAsync(SuiteResult result, CancellationToken cancellationToken)
    {
        var outputPath = Output is null
            ? System.IO.Path.GetFullPath(System.IO.Path.Combine("tests", "benchmarks", "results", "resident-latency.json"))
            : System.IO.Path.GetFullPath(Output);
        await Reporting.WriteAsync(result, outputPath, cancellationToken);
        _consoleUI.WriteStep($"Wrote results to {outputPath}");
    }

    private static string Description() => "S1 resident delta-check latency: warm time, RSS, and overlay-check P50/P95.";

    private static Scorecard Empty() => new(0, 0, 0, 0, 0, 0, 0, 0);

    private static async Task<bool> BuildBinlogAsync(string target, string binlogPath, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = System.IO.Path.GetDirectoryName(target) ?? Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add($"-bl:{binlogPath}");
        psi.ArgumentList.Add("-nologo");
        psi.ArgumentList.Add("-v:quiet");

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }

        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
        _ = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return false;
        }

        return File.Exists(binlogPath);
    }
}
