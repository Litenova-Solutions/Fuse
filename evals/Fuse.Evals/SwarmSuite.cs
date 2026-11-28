using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
namespace Fuse.Evals;

/// <summary>
///     Measures Fuse 7.0.0 swarm capabilities on a repository: shared hypervisor RAM across git worktrees and
///     zero-restore check latency, <c>fuse slim</c>, <c>fuse check --brief</c>, <c>fuse gate --brief --test</c>,
///     <c>fuse judge --combine</c> and <c>--race</c>, <c>fuse batch</c> throughput, <c>fuse graph --json</c>,
///     <c>fuse impact --json</c>, and hardlink/MTP shadow test runs.
/// </summary>
internal static class SwarmSuite
{
    private const int SecondaryWorktrees = 3;
    private const int BriefRounds = 5;
    private const int GateRounds = 3;
    private const int JudgeRounds = 3;
    private const int BatchRequests = 20;
    private const int GraphRounds = 5;
    private const int ShadowRounds = 3;

    public static async Task<object> RunAsync(EvalRepo repo, SolutionInfo solution)
    {
        if (!await repo.IsCleanAsync())
            throw new InvalidOperationException($"{repo.Root} has uncommitted changes; the suite needs a clean tree");

        var build = await repo.BuildAsync();
        if (build.ExitCode != 0)
            throw new InvalidOperationException($"HEAD does not build: {string.Join("; ", build.Errors.Take(3))}");

        var targets = PickTargets(solution);
        if (targets.Count == 0)
            throw new InvalidOperationException($"no editable C# file with a method body found in {solution.Root}");

        var primaryTarget = targets[0];
        var secondaryTarget = targets.Count > 1 ? targets[1] : primaryTarget;
        var testTarget = PickTestCoveredTarget(solution, targets) ?? primaryTarget;

        var tracked = targets.Concat([testTarget]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var originalBytes = tracked.ToDictionary(f => f, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        var originalTexts = tracked.ToDictionary(f => f, f => File.ReadAllText(f), StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            await repo.KillEngineAsync();
            var coldPrimary = await repo.FuseTimedAsync("check");
            Console.WriteLine($"[swarm] primary cold check: {coldPrimary.Milliseconds:0} ms");

            await File.WriteAllTextAsync(primaryTarget, InjectStatement(originalTexts[primaryTarget], "_ = 1;"));
            await repo.FuseTimedAsync("check", primaryTarget);
            await File.WriteAllBytesAsync(primaryTarget, originalBytes[primaryTarget]);
            await repo.FuseTimedAsync("check");

            var singleStatus = await repo.StatusEngineAsync(repo.Root);
            var singleProcs = await repo.EngineProcessesAsync();
            var singleCount = singleProcs.Count > 0 ? singleProcs.Count : (singleStatus is not null ? 1 : 0);
            var singleWsMb = Mb(singleStatus?.WorkingSetBytes ?? (singleProcs.Count > 0 ? singleProcs.Max(e => e.WorkingSet) : 0));
            var singlePeakMb = Mb(singleStatus?.PeakWorkingSetBytes ?? (singleProcs.Count > 0 ? singleProcs.Max(e => e.PeakWorkingSet) : 0));

            var worktrees = await MeasureWorktreesAsync(
                repo,
                primaryTarget,
                originalTexts[primaryTarget],
                originalBytes[primaryTarget],
                singleCount,
                singleWsMb,
                singlePeakMb);

            var slim = await MeasureSlimAsync(repo);
            var checkBrief = await MeasureCheckBriefAsync(repo, primaryTarget, originalTexts[primaryTarget], originalBytes[primaryTarget]);
            var gate = await MeasureGateAsync(repo, testTarget, originalTexts[testTarget], originalBytes[testTarget]);
            var judge = await MeasureJudgeAsync(repo, primaryTarget, secondaryTarget, originalTexts, originalBytes);
            var batch = await MeasureBatchAsync(repo);
            var graph = await MeasureGraphAsync(repo, primaryTarget);
            var shadow = await MeasureShadowAsync(repo, testTarget, originalTexts[testTarget], originalBytes[testTarget], taken);

            // Wait once at the end so the shared hypervisor's idle trimming (30 s idle, 15 s poll) runs cleanly.
            await Task.Delay(TimeSpan.FromSeconds(50));
            var idleStatus = await repo.StatusEngineAsync(repo.Root);
            var idleProcs = await repo.EngineProcessesAsync();
            var idleMb = Mb(idleStatus?.WorkingSetBytes ?? (idleProcs.Count > 0 ? idleProcs.Max(e => e.WorkingSet) : 0));
            worktrees = worktrees with { MultiWorktreeIdleWorkingSetMb = Math.Round(idleMb, 1) };

            var result = new SwarmResult(
                "swarm",
                repo.Name,
                await repo.HeadAsync(),
                await repo.VersionAsync(),
                await repo.SdkVersionAsync(),
                worktrees,
                slim,
                checkBrief,
                gate,
                judge,
                batch,
                graph,
                shadow,
                await repo.IsCleanAsync());

            Console.WriteLine(
                $"[swarm] worktrees RAM: single {singleWsMb:0} MB vs {SecondaryWorktrees + 1} worktrees {worktrees.MultiWorktreeWorkingSetMb:0} MB ({worktrees.SharedEnginePidCount} PID); " +
                $"zero-restore P50 {worktrees.ZeroRestoreEditCheck.P50:0} ms; brief P50 {checkBrief.Latency.P50:0} ms; batch {batch.TotalMs:0} ms ({batch.Responses}/{batch.Requests})");
            return result;
        }
        finally
        {
            foreach (var (file, bytes) in originalBytes)
                await File.WriteAllBytesAsync(file, bytes);
        }
    }

    private static async Task<SwarmResult.WorktreeMeasure> MeasureWorktreesAsync(
        EvalRepo repo,
        string primaryTarget,
        string originalText,
        byte[] originalBytes,
        int singleCount,
        double singleWsMb,
        double singlePeakMb)
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"fuse-eval-wt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(baseDir);
        var created = new List<(string Dir, string Branch)>();
        var relTarget = Path.GetRelativePath(repo.Root, primaryTarget);
        var coldChecks = new List<double>();
        var editChecks = new List<double>();
        var pids = new HashSet<int>();

        try
        {
            if (await repo.StatusEngineAsync(repo.Root) is { } primaryStatus)
                pids.Add(primaryStatus.Pid);

            for (var i = 1; i <= SecondaryWorktrees; i++)
            {
                var wtDir = Path.Combine(baseDir, $"wt-{i}");
                var branch = $"eval-swarm-wt-{i}-{Guid.NewGuid().ToString("N")[..6]}";
                var add = await repo.GitAsync("worktree", "add", "-q", "-b", branch, wtDir, "HEAD");
                if (add.ExitCode != 0)
                    throw new InvalidOperationException($"git worktree add failed: {add.Output}");
                created.Add((wtDir, branch));

                var cold = await repo.FuseInAsync(wtDir, "check");
                coldChecks.Add(cold.Milliseconds);
                if (await repo.StatusEngineAsync(wtDir) is { } wtStatus)
                    pids.Add(wtStatus.Pid);

                var wtFile = Path.Combine(wtDir, relTarget);
                await File.WriteAllTextAsync(wtFile, InjectStatement(originalText, $"_ = {10 + i};"));
                var edit = await repo.FuseInAsync(wtDir, "check", wtFile);
                editChecks.Add(edit.Milliseconds);
                await File.WriteAllBytesAsync(wtFile, originalBytes);
            }

            var multiStatus = await repo.StatusEngineAsync(repo.Root);
            var multiProcs = await repo.EngineProcessesAsync();
            var multiCount = multiProcs.Count > 0 ? multiProcs.Count : (multiStatus is not null ? 1 : 0);
            var multiWsMb = Mb(multiStatus?.WorkingSetBytes ?? (multiProcs.Count > 0 ? multiProcs.Max(e => e.WorkingSet) : 0));
            var multiPeakMb = Mb(multiStatus?.PeakWorkingSetBytes ?? (multiProcs.Count > 0 ? multiProcs.Max(e => e.PeakWorkingSet) : 0));
            var totalWorktrees = SecondaryWorktrees + 1;

            return new SwarmResult.WorktreeMeasure(
                SecondaryWorktrees,
                singleCount,
                Math.Round(singleWsMb, 1),
                Math.Round(singlePeakMb, 1),
                multiCount,
                pids.Count,
                Math.Round(multiWsMb, 1),
                Math.Round(multiPeakMb, 1),
                0,
                Math.Round(multiWsMb / totalWorktrees, 1),
                LatencyStats.Of(coldChecks),
                LatencyStats.Of(editChecks));
        }
        finally
        {
            foreach (var (wtDir, branch) in created)
            {
                await repo.GitAsync("worktree", "remove", "--force", wtDir);
                await repo.GitAsync("branch", "-D", branch);
            }

            await repo.GitAsync("worktree", "prune");
            try
            {
                Directory.Delete(baseDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<SwarmResult.SlimMeasure> MeasureSlimAsync(EvalRepo repo)
    {
        var before = await repo.StatusEngineAsync(repo.Root);
        var beforeMb = Mb(before?.WorkingSetBytes ?? 0);
        var run = await repo.FuseTimedAsync("slim");
        var after = await repo.StatusEngineAsync(repo.Root);
        var afterMb = Mb(after?.WorkingSetBytes ?? 0);
        return new SwarmResult.SlimMeasure(
            Math.Round(run.Milliseconds, 1),
            run.ExitCode,
            Math.Round(beforeMb, 1),
            Math.Round(afterMb, 1),
            Math.Round(Math.Max(0, beforeMb - afterMb), 1));
    }

    private static async Task<SwarmResult.RepairCheckMeasure> MeasureCheckBriefAsync(EvalRepo repo, string file, string originalText, byte[] originalBytes)
    {
        var latencies = new List<double>();
        var hints = 0;
        try
        {
            for (var i = 1; i <= BriefRounds; i++)
            {
                await File.WriteAllTextAsync(file, InjectStatement(originalText, $"_ = {i}; ((object)0).ToStrin();"));
                var run = await repo.FuseTimedAsync("check", file, "--brief");
                latencies.Add(run.Milliseconds);
                if (run.Output.Contains("CS1061", StringComparison.Ordinal) && run.Output.Contains("ToString", StringComparison.Ordinal))
                    hints++;
            }
        }
        finally
        {
            await File.WriteAllBytesAsync(file, originalBytes);
            await repo.FuseAsync("check");
        }

        return new SwarmResult.RepairCheckMeasure(
            Path.GetRelativePath(repo.Root, file).Replace('\\', '/'),
            LatencyStats.Of(latencies),
            hints,
            BriefRounds);
    }

    private static async Task<SwarmResult.GateMeasure> MeasureGateAsync(EvalRepo repo, string file, string originalText, byte[] originalBytes)
    {
        double failMs;
        int failExit;
        var passMs = new List<double>();
        var passFailures = 0;
        var passFailureOutputs = new List<string>();

        try
        {
            await File.WriteAllTextAsync(file, InjectStatement(originalText, "((object)0).ToStrin();"));
            var failRun = await repo.FuseTimedAsync("gate", "--brief", "--test");
            failMs = failRun.Milliseconds;
            failExit = failRun.ExitCode;

            for (var i = 1; i <= GateRounds; i++)
            {
                await File.WriteAllTextAsync(file, InjectStatement(originalText, $"_ = {200 + i};"));
                var passRun = await repo.FuseTimedAsync("gate", "--brief", "--test");
                passMs.Add(passRun.Milliseconds);
                if (passRun.ExitCode != 0)
                {
                    passFailures++;
                    passFailureOutputs.Add($"exit {passRun.ExitCode}: {passRun.Output[..Math.Min(2000, passRun.Output.Length)]}");
                }
            }
        }
        finally
        {
            await File.WriteAllBytesAsync(file, originalBytes);
            await repo.FuseAsync("check");
        }

        return new SwarmResult.GateMeasure(Math.Round(failMs, 1), failExit, LatencyStats.Of(passMs), passFailures, passFailureOutputs);
    }

    private static async Task<SwarmResult.JudgeMeasure> MeasureJudgeAsync(
        EvalRepo repo,
        string file1,
        string file2,
        Dictionary<string, string> texts,
        Dictionary<string, byte[]> bytes)
    {
        var patchDir = Path.Combine(Path.GetTempPath(), $"fuse-eval-judge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(patchDir);
        var combineTimes = new List<double>();
        var raceTimes = new List<double>();
        var combinePassed = true;
        var racePassed = true;

        try
        {
            var patch1Text = InjectStatement(texts[file1], "_ = 301;");
            var patch2Text = file1.Equals(file2, StringComparison.OrdinalIgnoreCase)
                ? InjectStatement(texts[file1], "_ = 302;", methodIndex: 1)
                : InjectStatement(texts[file2], "_ = 302;");
            var badText = InjectStatement(texts[file1], "((object)0).ToStrin();");

            var p1 = await WritePatchAsync(repo, file1, patch1Text, bytes[file1], Path.Combine(patchDir, "p1.patch"));
            var p2 = await WritePatchAsync(repo, file2, patch2Text, bytes[file2], Path.Combine(patchDir, "p2.patch"));
            var bad = await WritePatchAsync(repo, file1, badText, bytes[file1], Path.Combine(patchDir, "bad.patch"));

            for (var i = 0; i < JudgeRounds; i++)
            {
                var combine = await repo.FuseTimedAsync("judge", "--combine", p1, p2, "--json");
                combineTimes.Add(combine.Milliseconds);
                if (combine.ExitCode != 0)
                    combinePassed = false;

                var race = await repo.FuseTimedAsync("judge", "--race", p1, bad, "--json");
                raceTimes.Add(race.Milliseconds);
                if (race.ExitCode != 0)
                    racePassed = false;
            }
        }
        finally
        {
            await File.WriteAllBytesAsync(file1, bytes[file1]);
            await File.WriteAllBytesAsync(file2, bytes[file2]);
            try
            {
                Directory.Delete(patchDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        return new SwarmResult.JudgeMeasure(
            LatencyStats.Of(combineTimes),
            combinePassed,
            LatencyStats.Of(raceTimes),
            racePassed);
    }

    private static async Task<SwarmResult.BatchMeasure> MeasureBatchAsync(EvalRepo repo)
    {
        var lines = new StringBuilder();
        for (var i = 0; i < BatchRequests; i++)
        {
            var line = (i % 3) switch
            {
                0 => """{"request":"Ping"}""",
                1 => """{"request":"Status"}""",
                _ => """{"request":"CheckChanges","waitForLoad":true}""",
            };
            lines.AppendLine(line);
        }

        var run = await repo.FuseStdinAsync(repo.Root, lines.ToString(), "batch");
        var responses = run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        return new SwarmResult.BatchMeasure(
            BatchRequests,
            responses,
            Math.Round(run.Milliseconds, 1),
            Math.Round(run.Milliseconds / BatchRequests, 2),
            run.ExitCode);
    }

    private static async Task<SwarmResult.GraphMeasure> MeasureGraphAsync(EvalRepo repo, string targetFile)
    {
        var graphTimes = new List<double>();
        var impactTimes = new List<double>();
        var graphBytes = 0;
        var impactBytes = 0;
        var relPath = Path.GetRelativePath(repo.Root, targetFile).Replace('\\', '/');

        for (var i = 0; i < GraphRounds; i++)
        {
            var graph = await repo.FuseTimedAsync("graph", "--json");
            graphTimes.Add(graph.Milliseconds);
            graphBytes = Encoding.UTF8.GetByteCount(graph.Output);

            var impact = await repo.FuseTimedAsync("impact", relPath, "--json");
            impactTimes.Add(impact.Milliseconds);
            impactBytes = Encoding.UTF8.GetByteCount(impact.Output);
        }

        return new SwarmResult.GraphMeasure(
            LatencyStats.Of(graphTimes),
            graphBytes,
            LatencyStats.Of(impactTimes),
            impactBytes);
    }

    private static async Task<SwarmResult.ShadowMeasure> MeasureShadowAsync(
        EvalRepo repo,
        string testTarget,
        string originalText,
        byte[] originalBytes,
        HashSet<string> taken)
    {
        var times = new List<double>();
        var calls = new List<TimedCall>();
        var shadowRuns = 0;
        var msbuildRuns = 0;

        try
        {
            // Warm up test execution once before timing the 3 measured rounds.
            await File.WriteAllTextAsync(testTarget, InjectStatement(originalText, "_ = 400;"));
            await repo.FuseTimedAsync("test");

            for (var i = 1; i <= ShadowRounds; i++)
            {
                await File.WriteAllTextAsync(testTarget, InjectStatement(originalText, $"_ = {400 + i};"));
                var logStart = repo.EngineLogLines().Count;
                var run = await repo.FuseTimedAsync("test");
                times.Add(run.Milliseconds);
                var lines = repo.EngineLogLines().Skip(logStart).ToList();
                calls.Add(new TimedCall(run.Milliseconds, RequestPhases.TakeNewest(repo.EngineLogLines(), taken)));
                var counts = TestPlanCounts.Parse(run.Output, lines);
                shadowRuns += counts.ShadowRuns;
                msbuildRuns += counts.MsbuildRuns;
            }
        }
        finally
        {
            await File.WriteAllBytesAsync(testTarget, originalBytes);
        }

        return new SwarmResult.ShadowMeasure(
            Path.GetRelativePath(repo.Root, testTarget).Replace('\\', '/'),
            LatencyStats.Of(times),
            shadowRuns,
            msbuildRuns,
            PhaseReport.Build(calls));
    }

    private static async Task<string> WritePatchAsync(EvalRepo repo, string file, string updatedText, byte[] originalBytes, string patchPath)
    {
        await File.WriteAllTextAsync(file, updatedText);
        var rel = Path.GetRelativePath(repo.Root, file).Replace('\\', '/');
        var diff = await repo.GitAsync("diff", "--no-color", "--", rel);
        await File.WriteAllBytesAsync(file, originalBytes);
        if (diff.ExitCode != 0 || string.IsNullOrWhiteSpace(diff.Output))
            throw new InvalidOperationException($"git diff produced empty patch for {rel}");
        await File.WriteAllTextAsync(patchPath, diff.Output);
        return patchPath;
    }

    private static List<string> PickTargets(SolutionInfo solution)
    {
        var result = new List<string>();
        foreach (var project in solution.CodeProjects.OrderByDescending(solution.DependentCount))
        {
            var dir = Path.GetDirectoryName(project)!;
            var candidate = solution.CodeSources()
                .Where(f => string.Equals(solution.ProjectOf(f), dir, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .FirstOrDefault(f => HasMethodBody(File.ReadAllText(f)));
            if (candidate is not null)
                result.Add(candidate);
            if (result.Count >= 2)
                break;
        }

        return result;
    }

    private static string? PickTestCoveredTarget(SolutionInfo solution, List<string> fallback)
    {
        var testContents = solution.TestProjects
            .SelectMany(t => Directory.EnumerateFiles(Path.GetDirectoryName(t)!, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
            .Select(File.ReadAllText)
            .ToList();

        foreach (var file in solution.CodeSources())
        {
            var text = File.ReadAllText(file);
            if (!HasMethodBody(text))
                continue;
            var types = CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                .Select(t => t.Identifier.Text).ToList();
            if (types.Any(type => testContents.Any(t => t.Contains(type, StringComparison.Ordinal))))
                return file;
        }

        return fallback.FirstOrDefault();
    }

    private static bool HasMethodBody(string text) =>
        CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Any(m => m.Body is { Statements.Count: > 0 });

    private static string InjectStatement(string text, string statementText, int methodIndex = 0)
    {
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Body is { Statements.Count: > 0 })
            .ToList();
        var method = methodIndex < methods.Count ? methods[methodIndex] : methods[0];
        // Indented like the statement it goes before and ended with the file's own line ending, so a repository that
        // enforces layout (Jellyfin treats StyleCop's SA1137 as an error) sees no error the edit did not mean to make.
        var indent = method.Body!.Statements[0].GetLeadingTrivia().LastOrDefault(t => t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.WhitespaceTrivia));
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        // The text can hold several statements; parsed as a block, each one is inserted on its own line.
        var statements = ((BlockSyntax)SyntaxFactory.ParseStatement("{" + statementText + "}")).Statements
            .Select(s => s.WithLeadingTrivia(indent).WithTrailingTrivia(SyntaxFactory.EndOfLine(newline)));
        return root.ReplaceNode(method.Body!, method.Body!.WithStatements(method.Body.Statements.InsertRange(0, statements))).ToFullString();
    }

    private static double Mb(long bytes) => bytes / (1024.0 * 1024.0);
}
