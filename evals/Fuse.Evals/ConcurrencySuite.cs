using System.Diagnostics;
using System.Globalization;
using System.Text;
using Fuse.Dotnet;
using Fuse.Telemetry;

namespace Fuse.Evals;

/// <summary>
///     What simultaneous callers cost on a benchmark repository: waves of 1, 4, 10 and 20 callers from distinct
///     sessions, as post-edit checks, scoped checks, stops and test plans, fired together, with a
///     <c>fuse status</c> probe per caller fired with each wave. Each wave records the callers' wall times, the
///     queue time and engine time of its requests, the errors it reported, its timeouts, its
///     <c>Busy</c> answers and the engine's peak working set; the suite records the cold-start time before the
///     waves and the engine's idle working set after them. Hooks stay silent on timeouts and <c>Busy</c> answers,
///     so those come from the engine log. It needs no truth: a test-plan caller runs
///     <c>fuse test</c> as an agent does, and its plan is in its phase line.
/// </summary>
internal static class ConcurrencySuite
{
    /// <summary>How many callers each wave runs at once.</summary>
    private static readonly int[] CallerCounts = [1, 4, 10, 20];

    /// <summary>The forms a wave's callers take, in the order they are handed out, so a wave of four holds one of each.</summary>
    private static readonly string[] Kinds = ["post-edit", "scoped", "stop", "test-plan"];

    private const string PostEdit = "post-edit";
    private const string Scoped = "scoped";
    private const string Stop = "stop";
    private const string TestPlan = "test-plan";

    /// <summary>Measures every caller count on <paramref name="repo"/> and returns the result file's content.</summary>
    public static async Task<object> RunAsync(EvalRepo repo, SolutionInfo solution)
    {
        if (!await repo.IsCleanAsync())
            throw new InvalidOperationException($"{repo.Root} has uncommitted changes; the suite needs a clean tree");
        var build = await repo.BuildAsync();
        if (build.ExitCode != 0)
            throw new InvalidOperationException($"HEAD does not build: {string.Join("; ", build.Errors.Take(3))}");
        var targets = Targets(solution);
        if (targets.Count == 0)
            throw new InvalidOperationException($"the concurrency suite needs a file with a method body; found none in {solution.Root}");
        // The bytes are kept as well: a file with a byte order mark does not survive a text round trip, and the next suite
        // to run on this repository needs a clean tree.
        var originals = targets.ToDictionary(f => f, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        var texts = originals.ToDictionary(p => p.Key, p => File.ReadAllText(p.Key), StringComparer.OrdinalIgnoreCase);
        try
        {
            await repo.KillEngineAsync();
            var cold = await repo.FuseTimedAsync("check");
            Console.WriteLine($"[concurrency] cold check of a clean tree: {cold.Milliseconds:0} ms");

            // One edited check before the waves, so the first wave measures a warm engine, as the later waves find it.
            await File.WriteAllTextAsync(targets[0], MultiAgentScenario.TextEdit(texts[targets[0]], "900"));
            await repo.FuseTimedAsync("check", targets[0]);
            File.WriteAllBytes(targets[0], originals[targets[0]]);
            await repo.FuseAsync("check");

            var levels = new List<ConcurrencyLevel>();
            for (var wave = 0; wave < CallerCounts.Length; wave++)
                levels.Add(await WaveAsync(repo, targets, texts, originals, CallerCounts[wave], wave + 1));

            // An engine returns the memory its requests freed once idle for 30 s, and looks every 15 s, so 50 s covers both.
            await Task.Delay(TimeSpan.FromSeconds(50));
            var idleEngines = await repo.EngineProcessesAsync();
            var idle = idleEngines.Count > 0 ? idleEngines.Max(e => e.WorkingSet) / (1024.0 * 1024.0) : 0;
            var summary = new
            {
                suite = "concurrency",
                repo = repo.Name,
                commit = await repo.HeadAsync(),
                fuseBuild = await repo.VersionAsync(),
                coldStartMs = cold.Milliseconds,
                levels,
                enginePeakWorkingSetMb = levels.Count > 0 ? levels.Max(l => l.EnginePeakWorkingSetMb) : 0,
                engineIdleWorkingSetMb = Math.Round(idle, 1),
                treeCleanAfter = await repo.IsCleanAsync(),
            };
            Console.WriteLine($"[concurrency] cold start {cold.Milliseconds:0} ms; engine peak {summary.enginePeakWorkingSetMb:0} MB, {idle:0} MB once idle");
            return summary;
        }
        finally
        {
            foreach (var (file, bytes) in originals)
                File.WriteAllBytes(file, bytes);
        }
    }

    /// <summary>
    ///     One wave: a body edit in each caller's file, then every caller and one status probe per caller released
    ///     together. Callers that share a file on a repository with fewer editable files than callers share its edit;
    ///     their sessions stay distinct. The files are restored afterwards, and a check of the restored tree lets
    ///     the engine forget the sessions that wrote them.
    /// </summary>
    private static async Task<ConcurrencyLevel> WaveAsync(EvalRepo repo, List<string> targets, Dictionary<string, string> texts, Dictionary<string, byte[]> originals, int callers, int wave)
    {
        var used = Enumerable.Range(0, callers).Select(i => targets[i % targets.Count]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            foreach (var file in used)
                await File.WriteAllTextAsync(file, MultiAgentScenario.TextEdit(texts[file], (100 + wave).ToString(CultureInfo.InvariantCulture)));
            await Task.Delay(500);
            var logStart = repo.EngineLogLines().Count;
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = new List<Task<ConcurrencyCall>>();
            for (var i = 0; i < callers; i++)
                calls.Add(CallAsync(repo, Kinds[i % Kinds.Length], $"eval-concurrency-{callers}-{i + 1}", targets[i % targets.Count], start.Task));
            var probes = Enumerable.Range(0, callers).Select(_ => StatusAsync(repo, start.Task)).ToList();
            var watch = Stopwatch.StartNew();
            start.SetResult(true);
            var results = await Task.WhenAll(calls);
            var status = await Task.WhenAll(probes);
            var wallMs = watch.Elapsed.TotalMilliseconds;

            var lines = repo.EngineLogLines().Skip(logStart).ToList();
            var taken = new HashSet<string>(StringComparer.Ordinal);
            var phases = new List<RequestPhases>();
            while (RequestPhases.TakeNewest(lines, taken) is { } phase)
                phases.Add(phase);
            // Hooks report neither timeouts nor Busy answers in their output, so their signals are read back from
            // the engine log by session; commands carry theirs in their output, as CallAsync recorded them.
            var signals = ConcurrencySignals.Parse(lines, results.Select(c => c.Session).ToList());
            var reconciled = results.Select(c => c.Kind is PostEdit or Stop
                ? c with
                {
                    TimedOut = signals.TimedOutSessions.Contains(c.Session),
                    Busy = signals.BusySessions.Contains(c.Session),
                    Unanswered = signals.TimedOutSessions.Contains(c.Session),
                }
                : c).ToArray();
            var engines = await repo.EngineProcessesAsync();
            var peak = engines.Count > 0 ? Math.Round(engines.Max(e => e.PeakWorkingSet) / (1024.0 * 1024.0), 1) : 0;
            var callerPhases = phases.Where(p => CallerKind(p.Kind) is not null).ToList();
            var level = new ConcurrencyLevel(
                callers,
                wallMs,
                LatencyStats.Of([.. reconciled.Select(c => c.WallMs)]),
                LatencyStats.Of(GateTimes(callerPhases)),
                LatencyStats.Of(EngineTimes(callerPhases)),
                GateByHolder(callerPhases),
                [.. reconciled],
                reconciled.Sum(c => c.Errors),
                reconciled.Count(c => c.TimedOut),
                reconciled.Count(c => c.Busy),
                reconciled.Count(c => c.Unanswered),
                reconciled.Count(c => c.Kind == Stop && c.TimedOut),
                status.Count(p => p.TimedOut),
                ByKind(reconciled, phases),
                StatusOf(status, phases),
                peak);
            Console.WriteLine($"[concurrency] {callers} caller(s) in {wallMs:0} ms: queue P50 {level.Queue.P50:0} / P95 {level.Queue.P95:0} ms, engine P50 {level.Engine.P50:0} / P95 {level.Engine.P95:0} ms, {level.Errors} error(s), {level.Timeouts} timeout(s), {level.BusyAnswers} Busy answer(s), {level.Unanswered} unanswered, status {level.StatusTimeouts} timeout(s), engine peak {peak:0} MB");
            return level;
        }
        finally
        {
            foreach (var file in used)
                File.WriteAllBytes(file, originals[file]);
            // A check of the restored tree lets the engine forget the sessions that wrote these files.
            await repo.FuseAsync("check");
        }
    }

    /// <summary>The wall, queue, engine and answer counts of each form of caller in a wave.</summary>
    private static Dictionary<string, ConcurrencyKindStats> ByKind(ConcurrencyCall[] results, List<RequestPhases> phases)
    {
        var byKind = new Dictionary<string, ConcurrencyKindStats>(StringComparer.Ordinal);
        foreach (var kind in Kinds)
        {
            var calls = results.Where(c => c.Kind == kind).ToList();
            var requests = phases.Where(p => CallerKind(p.Kind) == kind).ToList();
            byKind[kind] = new ConcurrencyKindStats(
                calls.Count,
                LatencyStats.Of([.. calls.Select(c => c.WallMs)]),
                LatencyStats.Of(GateTimes(requests)),
                LatencyStats.Of(EngineTimes(requests)),
                calls.Sum(c => c.Errors),
                calls.Count(c => c.TimedOut),
                calls.Count(c => c.Busy),
                calls.Count(c => c.Unanswered));
        }

        return byKind;
    }

    /// <summary>The status probes of a wave, with the queue and engine times of their <c>Status</c> requests.</summary>
    private static ConcurrencyStatus StatusOf((double Ms, bool TimedOut, bool Busy, bool Unanswered)[] probes, List<RequestPhases> phases)
    {
        var requests = phases.Where(p => p.Kind == "Status").ToList();
        return new ConcurrencyStatus(
            probes.Length,
            LatencyStats.Of([.. probes.Select(p => p.Ms)]),
            LatencyStats.Of(GateTimes(requests)),
            LatencyStats.Of(EngineTimes(requests)),
            probes.Count(p => p.TimedOut),
            probes.Count(p => p.Busy),
            probes.Count(p => p.Unanswered));
    }

    /// <summary>What held the request lock longest during each wait in a wave, with the waits behind it.</summary>
    private static Dictionary<string, LatencyStats> GateByHolder(List<RequestPhases> phases) =>
        phases.Where(p => p.GateHolder is not null && p.Phases.ContainsKey(Phase.Gate))
            .GroupBy(p => p.GateHolder!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => LatencyStats.Of([.. g.Select(p => p.Phases[Phase.Gate])]), StringComparer.Ordinal);

    /// <summary>The <c>gate</c> times of <paramref name="phases"/>: how long each request waited for the request lock.</summary>
    private static List<double> GateTimes(IEnumerable<RequestPhases> phases) =>
        [.. phases.Where(p => p.Phases.ContainsKey(Phase.Gate)).Select(p => p.Phases[Phase.Gate])];

    /// <summary>The <c>total</c> times of <paramref name="phases"/>: how long the engine spent on each request.</summary>
    private static List<double> EngineTimes(IEnumerable<RequestPhases> phases) =>
        [.. phases.Where(p => p.Total is not null).Select(p => p.Total!.Value)];

    /// <summary>
    ///     The form of caller a phase line's case came from, or null for a case no caller in a wave sends. Each form
    ///     sends one case: a post-edit hook sends <c>CheckFiles</c>, a scoped check <c>CheckPaths</c>, a stop hook
    ///     <c>CheckChanges</c>, and a test plan <c>PlanAffectedTests</c>.
    /// </summary>
    private static string? CallerKind(string kind) => kind switch
    {
        "CheckFiles" => PostEdit,
        "CheckPaths" => Scoped,
        "CheckChanges" => Stop,
        "PlanAffectedTests" => TestPlan,
        "PlanAllTests" => TestPlan,
        _ => null,
    };

    /// <summary>One caller, released by <paramref name="start"/> with the rest of its wave, as <paramref name="kind"/> from <paramref name="session"/>.</summary>
    private static async Task<ConcurrencyCall> CallAsync(EvalRepo repo, string kind, string session, string file, Task start)
    {
        await start;
        var watch = Stopwatch.StartNew();
        string output;
        int exitCode;
        int errors;
        if (kind == PostEdit)
        {
            var result = await repo.FuseHookAsync("claude", "post-edit", PostEditPayload(repo, session, file));
            output = result.Output;
            exitCode = result.ExitCode;
            errors = EvalRepo.ParseFuseErrors(output).Count;
        }
        else if (kind == Stop)
        {
            var result = await repo.FuseHookAsync("claude", "stop", StopPayload(repo, session));
            output = result.Output;
            exitCode = result.ExitCode;
            var (_, reason) = EvalRepo.ParseStopBlock(output);
            errors = EvalRepo.ParseFuseErrors(reason).Count;
        }
        else if (kind == Scoped)
        {
            var run = await FuseSessionAsync(repo, session, ["check", Path.GetDirectoryName(file)!]);
            output = run.Output;
            exitCode = run.ExitCode;
            errors = EvalRepo.ParseFuseErrors(output).Count;
        }
        else
        {
            var run = await repo.FuseTimedAsync("test", "--session", session);
            output = run.Output;
            exitCode = run.ExitCode;
            errors = TestPlanCounts.RunCounts(output).Failed + EvalRepo.ParseFuseErrors(output).Count;
        }

        // A hook stays silent on a timeout and on a Busy answer, so its output carries neither; the wave reads
        // those signals back from the engine log and reconciles the hook callers there.
        var timedOut = kind is PostEdit or Stop ? false : IsTimedOut(output);
        var busy = kind is PostEdit or Stop ? false : IsBusy(output);
        // A post-edit hook reports its errors with exit code 2 to wake the agent, which is an answer, not an unanswered one.
        var unanswered = kind is PostEdit or Stop ? false : exitCode == 2 || timedOut;
        return new ConcurrencyCall(
            kind,
            session,
            Path.GetRelativePath(repo.Root, file).Replace('\\', '/'),
            watch.Elapsed.TotalMilliseconds,
            exitCode,
            errors,
            timedOut,
            busy,
            unanswered);
    }

    /// <summary>One <c>fuse status</c> probe, released by <paramref name="start"/> with a wave.</summary>
    private static async Task<(double Ms, bool TimedOut, bool Busy, bool Unanswered)> StatusAsync(EvalRepo repo, Task start)
    {
        await start;
        var run = await repo.FuseTimedAsync("status");
        var timedOut = IsTimedOut(run.Output);
        return (run.Milliseconds, timedOut, IsBusy(run.Output), run.ExitCode == 2 || timedOut);
    }

    /// <summary>
    ///     Runs fuse as <paramref name="session"/> through <c>FUSE_SESSION</c>, for the caller forms the command line
    ///     names no session for, as a post-edit hook credits its files. It runs the way
    ///     <see cref="EvalRepo.FuseHookAsync"/> runs a hook, with the session in the process's environment.
    /// </summary>
    private static async Task<FuseRun> FuseSessionAsync(EvalRepo repo, string session, string[] args)
    {
        var watch = Stopwatch.StartNew();
        var psi = new ProcessStartInfo(repo.Fuse)
        {
            WorkingDirectory = repo.Root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in args)
            psi.ArgumentList.Add(argument);
        psi.Environment["FUSE_SESSION"] = session;
        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new FuseRun(new ProcessResult(-1, $"could not start {repo.Fuse}: {e.Message}"), watch.Elapsed.TotalMilliseconds);
        }

        using var started = process;
        var output = started.StandardOutput.ReadToEndAsync();
        var error = started.StandardError.ReadToEndAsync();
        started.StandardInput.Close();
        await started.WaitForExitAsync();
        return new FuseRun(new ProcessResult(started.ExitCode, await output + await error), watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>The Claude Code post-edit payload for <paramref name="file"/>, from <paramref name="session"/>.</summary>
    private static string PostEditPayload(EvalRepo repo, string session, string file) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["hook_event_name"] = "PostToolUse",
            ["tool_name"] = "Edit",
            ["cwd"] = repo.Root,
            ["session_id"] = session,
            ["tool_input"] = new Dictionary<string, string> { ["file_path"] = file },
        });

    /// <summary>The Claude Code stop payload from <paramref name="session"/>.</summary>
    private static string StopPayload(EvalRepo repo, string session) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["hook_event_name"] = "Stop",
            ["cwd"] = repo.Root,
            ["session_id"] = session,
        });

    /// <summary>Whether a command answer says the engine did not answer in time, as a command's timeout and a status probe's wait both word it. Hooks stay silent, so theirs come from the engine log.</summary>
    private static bool IsTimedOut(string output) =>
        output.Contains("did not answer", StringComparison.OrdinalIgnoreCase) || output.Contains("timed out", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a command answer is a <c>Busy</c> answer. Hooks give <c>Busy</c> answers silently, so theirs come from the engine log.</summary>
    private static bool IsBusy(string output) => output.Contains("busy", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Up to twenty files a body edit changes, one at a time from each code project in turn, so a wave's callers
    ///     spread over projects before they share one. At most one hundred files of a project are read, which is
    ///     where its first editable files are.
    /// </summary>
    private static List<string> Targets(SolutionInfo solution)
    {
        var queues = new List<Queue<string>>();
        foreach (var group in solution.CodeSources().GroupBy(f => solution.ProjectOf(f) ?? Path.GetDirectoryName(f)!, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var queue = new Queue<string>(group.Order(StringComparer.Ordinal).Take(100).Where(Editable));
            if (queue.Count > 0)
                queues.Add(queue);
        }

        var result = new List<string>();
        while (result.Count < 20 && queues.Any(q => q.Count > 0))
        {
            foreach (var queue in queues)
            {
                if (result.Count == 20)
                    break;
                if (queue.Count > 0)
                    result.Add(queue.Dequeue());
            }
        }

        return result;
    }

    /// <summary>Whether a body edit would change <paramref name="file"/>, which needs a method with a body.</summary>
    private static bool Editable(string file)
    {
        var text = File.ReadAllText(file);
        return MultiAgentScenario.TextEdit(text, "1") != text;
    }
}

