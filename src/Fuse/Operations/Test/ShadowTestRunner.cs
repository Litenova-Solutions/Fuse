using System.Globalization;
using System.Text;
using Fuse.Client;
using Fuse.Dotnet.Processes;
using Fuse.Dotnet.Test;
using Fuse.Paths;
using Fuse.Protocol.Requests;
using Fuse.Protocol.Testing;
namespace Fuse.Operations.Test;

/// <summary>
///     Runs shadow test assemblies without MSBuild: shadow VSTest and Microsoft.Testing.Platform runs,
///     the slice cache hit and write, and the dynamic test map ingest.
/// </summary>
internal static class ShadowTestRunner
{
    private const int MaxTestMapSearchFolders = 8;

    private const int MaxTestMapFilesPerFolder = 64;

    private const int MaxTestMapsPerRun = 16;

    private const int MaxTestMapBytes = 8_000_000;

    /// <summary>How many failures a stored slice verdict carries at most, so the store request stays small.</summary>
    private const int MaxStoredFailures = 20;

    /// <summary>How long storing one slice verdict waits for the engine; the run is already answered without it.</summary>
    private static readonly TimeSpan SliceStoreTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     The outcome of a run the engine served from the slice cache, when its cached verdict passed every test, so
    ///     the run is reused without starting a process. Null when the run is not cached, when its verdict failed a
    ///     test (which runs fresh, so its failures print with their details), or when its reason parses to nothing.
    ///     The text is what the engine's plan mapper writes; the engine masks it under <c>--clean</c> and
    ///     <c>--no-cache</c>, so a masked run always reaches here uncached.
    /// </summary>
    internal static TestRunSummary? CachedPassOutcome(TestRun run)
    {
        if (!run.FromCache || run.CacheReason is not { } reason)
            return null;
        const string prefix = "slice cache: ";
        const string passed = " passed, ";
        const string failed = " failed, ";
        const string skipped = " skipped";
        if (!reason.StartsWith(prefix, StringComparison.Ordinal) || !reason.EndsWith(skipped, StringComparison.Ordinal))
            return null;
        var body = reason[prefix.Length..^skipped.Length];
        var first = body.IndexOf(passed, StringComparison.Ordinal);
        if (first < 0)
            return null;
        var second = body.IndexOf(failed, first + passed.Length, StringComparison.Ordinal);
        if (second < 0)
            return null;
        if (!int.TryParse(body[..first], NumberStyles.None, CultureInfo.InvariantCulture, out var passedCount)
            || !int.TryParse(body[(first + passed.Length)..second], NumberStyles.None, CultureInfo.InvariantCulture, out var failedCount)
            || !int.TryParse(body[(second + failed.Length)..], NumberStyles.None, CultureInfo.InvariantCulture, out var skippedCount))
            return null;
        return failedCount == 0 ? new TestRunSummary(passedCount, 0, skippedCount, []) : null;
    }

    /// <summary>
    ///     Runs one shadow run, or reuses its cached passing verdict without starting a process. A run that executes
    ///     stores its verdict in the slice cache, so the next identical plan reuses a passing verdict without running.
    /// </summary>
    internal static async Task<TestRunOutcome> RunShadowOrCachedAsync(RepoRoot root, TestRun run, int maxFailures, CancellationToken cancellationToken, bool noCache = false, TimeSpan? inactivityTimeout = null)
    {
        if (CachedPassOutcome(run) is { } reused)
            return new TestRunOutcome(reused, null, "", "", null);
        var started = Environment.TickCount64;
        var ran = run.UsesTestingPlatform
            ? await RunShadowPlatformAsync(root, run, maxFailures, cancellationToken, inactivityTimeout).ConfigureAwait(false)
            : await DotnetTestRunner.RunDotnetTestAsync(root, root.Path, [((TestRunMode.Shadow)run.Mode).Assembly], cancellationToken, assembly: true, run: run, maxFailures: maxFailures, inactivityTimeout: inactivityTimeout).ConfigureAwait(false);
        await StoreSliceVerdictAsync(root, run, ran.Outcome, Environment.TickCount64 - started, noCache, cancellationToken).ConfigureAwait(false);
        return ran;
    }

    /// <summary>
    ///     The store request for a finished shadow run's verdict, or null when the verdict is not stored: under
    ///     <c>--no-cache</c>, when the run has no slice key, when it ended without results or ran no test, or when
    ///     its counts were made from watched failure lines instead of a result file. A failed verdict is stored, so
    ///     the next plan sees it; the client runs a cached failure fresh anyway. Only a run of the plan's slice
    ///     reaches here with its key: a split stage runs under another filter with no key, and a run that fell back
    ///     to MSBuild stores nothing.
    /// </summary>
    internal static EngineRequest.StoreSliceResult? SliceStoreFor(TestRun run, TestRunSummary? outcome, double durationMs, bool noCache)
    {
        if (noCache || outcome is null || outcome.Total == 0 || string.IsNullOrWhiteSpace(run.SliceKey))
            return null;
        if (outcome.Failures.Any(f => f.Message == TestOperation.StoppedEarlyMessage))
            return null;
        return new EngineRequest.StoreSliceResult
        {
            SliceKey = run.SliceKey,
            Project = run.Project,
            ProjectName = run.Name,
            Passed = outcome.Passed,
            Failed = outcome.Failed,
            Skipped = outcome.Skipped,
            DurationMs = durationMs,
            Failures = [.. outcome.Failures.Take(MaxStoredFailures).Select(f => new EngineRequest.SliceFailure(f.Name, f.Message, [.. f.Frames]))],
        };
    }

    /// <summary>
    ///     Stores a finished shadow run's verdict in the engine's slice cache. Advisory: a verdict that is not
    ///     stored only costs the next identical plan its reuse, so a store never fails the run, never starts an
    ///     engine for it, and never stores for a cancelled run.
    /// </summary>
    internal static async Task StoreSliceVerdictAsync(RepoRoot root, TestRun run, TestRunSummary? outcome, double durationMs, bool noCache, CancellationToken cancellationToken)
    {
        var store = SliceStoreFor(run, outcome, durationMs, noCache);
        if (store is null || cancellationToken.IsCancellationRequested || !EngineClient.PipeExists(root.PipeName))
            return;
        try
        {
            // Detached from the run's cancellation: the run is over, and the timeout bounds the store on its own.
            await EngineClient.SendAsync(root, store, SliceStoreTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Advisory: the next identical plan runs fresh instead of reusing this verdict.
        }
    }

    /// <summary>
    ///     Copies the test maps a finished Microsoft.Testing.Platform run wrote into the incoming folder the engine's
    ///     next plan ingests, under a subfolder named for the test project. Runs only on the .NET 11 SDK, which is
    ///     the first to write them; otherwise records nothing. Never fails the run: maps are advisory, and a missed
    ///     one only costs the next selection its hints.
    /// </summary>
    internal static async Task CollectTestMapsAsync(RepoRoot root, TestRun? run, IReadOnlyList<string> searchDirectories, CancellationToken cancellationToken)
    {
        try
        {
            if (run is not { UsesTestingPlatform: true })
                return;
            var folders = searchDirectories.Where(Directory.Exists).Take(MaxTestMapSearchFolders).ToList();
            if (folders.Count == 0)
                return;
            if (!(await SdkCapabilities.DetectAsync(root.Path, cancellationToken).ConfigureAwait(false)).SupportsMtpAffectedTests)
                return;
            // The engine reads this folder without the client: its path and the hex-encoded project name below match
            // the dynamic map's incoming folder, which this layer may not name.
            var incoming = Path.Combine(root.SharedStateDirectory, "test-maps", "incoming", Convert.ToHexStringLower(Encoding.UTF8.GetBytes(root.PathOf(run.Project).Relative)));
            var copied = 0;
            foreach (var folder in folders)
            {
                foreach (var file in TestMapFiles(folder))
                {
                    if (copied >= MaxTestMapsPerRun)
                        return;
                    try
                    {
                        if (new FileInfo(file).Length > MaxTestMapBytes)
                            continue;
                        Directory.CreateDirectory(incoming);
                        File.Copy(file, Path.Combine(incoming, Path.GetFileName(file)), overwrite: true);
                        copied++;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // One unreadable map never stops the rest.
                    }
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Advisory: a missed map only costs the next selection its hints, never the run its answer.
        }
    }

    /// <summary>The test map files under <paramref name="folder"/>: its top level and its test-results folder, bounded.</summary>
    private static IEnumerable<string> TestMapFiles(string folder)
    {
        var files = EnumerateTopFiles(folder).Concat(EnumerateTopFiles(Path.Combine(folder, "TestResults"))).Take(MaxTestMapFilesPerFolder);
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (name.Contains("testmap", StringComparison.OrdinalIgnoreCase) || name.Contains("test-map", StringComparison.OrdinalIgnoreCase))
                yield return file;
        }
    }

    /// <summary>The files directly under <paramref name="folder"/>, or none when it cannot be read.</summary>
    private static IEnumerable<string> EnumerateTopFiles(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)] : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    ///     Runs one Microsoft.Testing.Platform shadow-emitted assembly directly without MSBuild. Returns null outcome
    ///     when the shadow assembly cannot execute directly so the caller falls back cleanly to MSBuild.
    /// </summary>
    internal static async Task<TestRunOutcome> RunShadowPlatformAsync(
        RepoRoot root, TestRun run, int maxFailures, CancellationToken cancellationToken, TimeSpan? inactivityTimeout = null)
    {
        var shadowAssembly = ((TestRunMode.Shadow)run.Mode).Assembly;
        if (!File.Exists(shadowAssembly))
            return new TestRunOutcome(null, null, "", "", null);
        if (shadowAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.ChangeExtension(shadowAssembly, ".runtimeconfig.json")))
            return new TestRunOutcome(null, null, "", "", null);

        var results = Path.Combine(root.StateDirectory, "results", Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string[] settings = run.Filter is not null || run.RunSettings is not null
                ? ["--settings", RunSettingsFile.Write(results, run.Filter, run.RunSettings)]
                : [];

            var arguments = new List<string>();
            string file;
            var shadowDirectory = Path.GetDirectoryName(shadowAssembly);
            ArgumentNullException.ThrowIfNull(shadowDirectory);
            var apphost = OperatingSystem.IsWindows()
                ? Path.ChangeExtension(shadowAssembly, ".exe")
                : Path.Combine(shadowDirectory, Path.GetFileNameWithoutExtension(shadowAssembly));
            if (shadowAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(apphost))
                {
                    file = apphost;
                }
                else
                {
                    file = "dotnet";
                    arguments.Add("exec");
                    arguments.Add(shadowAssembly);
                }
            }
            else
            {
                file = shadowAssembly;
            }

            arguments.AddRange(run.FilterArguments);
            arguments.AddRange(settings);
            var workingDirectory = Path.GetDirectoryName(shadowAssembly);
            ArgumentNullException.ThrowIfNull(workingDirectory);
            var (process, earlyFailures) = await DotnetTestRunner.RunWithEarlyStopAsync(file, [.. arguments], workingDirectory, maxFailures, cancellationToken, TestOperation.EffectiveTimeout(inactivityTimeout)).ConfigureAwait(false);
            if (process.TimedOut)
                return new TestRunOutcome(null, process, "", results, TargetFrameworkOf(run));
            var outcome = TestingPlatformOutput.Read(process.Output, root.Path)
                ?? XUnitOutput.Read(process.Output, root.Path)
                ?? TrxReader.ReadDirectory(results, root.Path);
            if (outcome is null && earlyFailures.Count > 0)
                outcome = FailureGroups.SynthesizeFailures(earlyFailures);
            if (outcome is { Failed: 0 } && process.ExitCode != 0)
                outcome = null;
            if (outcome is null)
                return new TestRunOutcome(null, process.ExitCode == 0 ? null : process, "", results, null);
            if (outcome.Total == 0 && (run.Filter is not null || run.FilterArguments.Length > 0))
                return new TestRunOutcome(null, new ProcessResult(TestResultRenderer.NoTestRanExitCode, process.Output), "", results, null);
            var shadowParent = Path.GetDirectoryName(shadowAssembly);
            ArgumentNullException.ThrowIfNull(shadowParent);
            await CollectTestMapsAsync(root, run, [results, shadowParent], cancellationToken).ConfigureAwait(false);
            return new TestRunOutcome(outcome, null, "", results, null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(results))
                    Directory.Delete(results, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    ///     The target framework a timed-out <paramref name="run"/> ran, for the timeout answer, or null when the run
    ///     covers more than one. A shadow run names its variant's framework after the project's name in its folder
    ///     (<c>Lib.Tests-net9.0</c>); a build run whose every target path was built for one framework ran that one.
    /// </summary>
    internal static string? TargetFrameworkOf(TestRun run)
    {
        if (run.Mode is TestRunMode.Shadow shadow)
        {
            // The assembly runs from a numbered copy under the folder that names the variant
            // (<c>Lib.Tests-net9.0/g1/Lib.Tests.dll</c>); the walk finds it at any depth.
            var prefix = run.Name + "-";
            for (var directory = Path.GetDirectoryName(shadow.Assembly);
                directory is not null;
                directory = Path.GetDirectoryName(directory))
            {
                var folder = Path.GetFileName(directory);
                if (folder is not null && folder.StartsWith(prefix, StringComparison.Ordinal) && IsFrameworkName(folder[prefix.Length..]))
                    return folder[prefix.Length..];
            }
        }

        return AgreedFramework(run.TargetPaths);
    }

    /// <summary>The one framework every path in <paramref name="targets"/> was built for, or null when they name none or disagree.</summary>
    internal static string? AgreedFramework(string[] targets)
    {
        string? agreed = null;
        foreach (var target in targets)
        {
            if (TargetFrameworkOfPath(target) is not { } framework)
                return null;
            if (agreed is null)
                agreed = framework;
            else if (!agreed.Equals(framework, StringComparison.OrdinalIgnoreCase))
                return null;
        }

        return agreed;
    }

    /// <summary>
    ///     The target framework the test application at <paramref name="target"/> was built for, from its output
    ///     folder (<c>net9.0</c>, or <c>debug_net9.0</c> under central output), or null when the folder names none.
    /// </summary>
    internal static string? TargetFrameworkOfPath(string target)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(target));
        if (folder is null)
            return null;
        var name = folder.Contains('_') ? folder[(folder.LastIndexOf('_') + 1)..] : folder;
        return IsFrameworkName(name) ? name : null;
    }

    /// <summary>Whether <paramref name="name"/> reads as a target framework (<c>net9.0</c>, <c>netstandard2.0</c>, <c>net481</c>).</summary>
    private static bool IsFrameworkName(string name) =>
        name.StartsWith("net", StringComparison.OrdinalIgnoreCase)
        && name.Length > 3
        && (char.IsDigit(name[3]) || name.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     A built test application's path under <paramref name="configuration"/>: evaluation names the default
    ///     configuration's output, so the segment a build writes the named one to takes its place.
    /// </summary>
    internal static string ForConfiguration(string target, string configuration)
    {
        var parts = target.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var bin = Array.FindIndex(parts, p => p.Equals("bin", StringComparison.OrdinalIgnoreCase));
        if (bin < 0 || bin + 1 >= parts.Length)
            return target;
        parts[bin + 1] = configuration;
        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    /// <summary>Whether <paramref name="path"/> has a segment that is exactly <paramref name="segment"/>.</summary>
    internal static bool HasSegment(string path, string segment) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p.Equals(segment, StringComparison.OrdinalIgnoreCase));
}
