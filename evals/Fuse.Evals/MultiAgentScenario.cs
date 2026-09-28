using System.Diagnostics;
using System.Text.RegularExpressions;
using Fuse.Dotnet;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Evals;

/// <summary>What four agents editing at once cost, and what one agent's check costs on its own.</summary>
internal sealed record MultiAgentWriters(
    int Clients,
    int EditsPerClient,
    double WallMs,
    int Checks,
    int Failed,
    int TimedOut,
    LatencyStats Gate,
    LatencyStats EngineTotal,
    double SingleClientTotalP50);

/// <summary>What three builds at once cost, and how many of them collided on a file.</summary>
internal sealed record MultiAgentBuilds(int Clients, int Rounds, double WallMs, LatencyStats Each, int Collisions, List<string> CollisionSamples);

/// <summary>One verify round: the project the edit was in, how long the test run took, and the test projects it ran.</summary>
internal sealed record MultiAgentRound(int Index, string Project, string Edit, double Ms, List<string> TestProjects, int FailingTests);

/// <summary>All three scenarios, as the latency result file's <c>multiAgent</c> object.</summary>
internal sealed record MultiAgentResult(MultiAgentWriters Writers, MultiAgentBuilds Builds, List<MultiAgentRound> Rounds);

/// <summary>
///     The three multi-agent scenarios the plan measures: several agents checking at once, several builds at once, and
///     three verify rounds over two projects. They answer whether a check queue is worth coalescing and whether a test
///     project is rerun for an edit that cannot reach it.
/// </summary>
internal static partial class MultiAgentScenario
{
    private const int Writers = 4;
    private const int EditsPerWriter = 10;
    private const int Builders = 3;
    private const int BuildRounds = 5;

    private static readonly string[] CollisionIds = ["MSB3021", "MSB3026", "MSB3027", "CS2012"];

    /// <summary>Runs the three scenarios against <paramref name="repo"/> and restores every file it edits.</summary>
    public static async Task<MultiAgentResult> RunAsync(EvalRepo repo, SolutionInfo solution, double singleClientTotalP50)
    {
        var targets = OneFilePerProject(solution, Writers);
        var writers = await WritersAsync(repo, targets, singleClientTotalP50);
        var builds = await BuildsAsync(repo);
        var rounds = await RoundsAsync(repo, solution);
        return new MultiAgentResult(writers, builds, rounds);
    }

    /// <summary>One source file from each of <paramref name="count"/> different projects, in a stable order.</summary>
    private static List<string> OneFilePerProject(SolutionInfo solution, int count)
    {
        var files = solution.CodeSources()
            .Where(f => f.Contains("public", StringComparison.Ordinal))
            .GroupBy(f => solution.ProjectOf(f) ?? "", StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.OrderBy(f => f, StringComparer.Ordinal).First())
            .ToList();
        return files.Count < count
            ? throw new InvalidOperationException($"need {count} projects with a public declaration; found {files.Count}")
            : files.Take(count).ToList();
    }

    /// <summary>
    ///     Four clients, each editing a file in a different project ten times and checking it, all at once. A fifth
    ///     client acts as the verifier: it checks and runs the tests three times while the writers work. The gate waits in
    ///     the engine's own phase lines are what say whether a queue is forming.
    /// </summary>
    private static async Task<MultiAgentWriters> WritersAsync(EvalRepo repo, List<string> targets, double singleClientTotalP50)
    {
        var originals = targets.ToDictionary(f => f, File.ReadAllText, StringComparer.OrdinalIgnoreCase);
        var walls = new List<double>();
        var failed = 0;
        var timedOut = 0;
        var gates = new List<double>();
        var totals = new List<double>();
        var checks = 0;

        var taken = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var watch = Stopwatch.StartNew();
            var clients = targets.Select(target => WriterAsync(repo, target, originals[target], taken, (ms, gate, total, ok, hung) =>
            {
                lock (walls)
                {
                    walls.Add(ms);
                    failed += ok ? 0 : 1;
                    timedOut += hung ? 1 : 0;
                    checks++;
                    if (gate is { } g)
                        gates.Add(g);
                    if (total is { } t)
                        totals.Add(t);
                }
            })).ToList();
            clients.Add(VerifierAsync(repo));
            await Task.WhenAll(clients);
            var wall = watch.Elapsed.TotalMilliseconds;

            Console.WriteLine($"[multiAgent] writers: {targets.Count} client(s) x {EditsPerWriter} edits in {wall:0} ms, {checks} check(s), {failed} failed, {timedOut} timed out");
            return new MultiAgentWriters(
                targets.Count,
                EditsPerWriter,
                wall,
                checks,
                failed,
                timedOut,
                Stats(gates),
                Stats(totals),
                singleClientTotalP50);
        }
        finally
        {
            foreach (var (file, text) in originals)
                File.WriteAllText(file, text);
        }
    }

    private static async Task WriterAsync(EvalRepo repo, string file, string original, HashSet<string> taken, Action<double, double?, double?, bool, bool> report)
    {
        for (var i = 1; i <= EditsPerWriter; i++)
        {
            await File.WriteAllTextAsync(file, BodyEdit(original, i));
            // The client is timed the way an agent's call would be: a process start, a pipe, a request, an answer.
            var watch = Stopwatch.StartNew();
            var run = await repo.FuseTimedAsync("check", file);
            var ms = watch.Elapsed.TotalMilliseconds;
            var phases = RequestPhases.TakeNewest(repo.EngineLogLines(), taken);
            var gate = phases?.Phases.GetValueOrDefault("gate");
            report(ms, gate, phases?.Total, run.ExitCode is 0 or 1, run.ExitCode == 2);
        }
    }

    /// <summary>The client an agent behaves like: it checks and then runs the tests, three times over.</summary>
    private static async Task VerifierAsync(EvalRepo repo)
    {
        for (var i = 0; i < 3; i++)
        {
            await repo.FuseTimedAsync("check");
            await repo.FuseTimedAsync("test");
        }
    }

    /// <summary>
    ///     Three clients running <c>fuse build</c> at once, five times over. The pass bar is zero collisions, not less
    ///     time: two builds that both succeed in sequence are the outcome worth having.
    /// </summary>
    private static async Task<MultiAgentBuilds> BuildsAsync(EvalRepo repo)
    {
        var walls = new List<double>();
        var collisions = 0;
        var samples = new List<string>();
        var watch = Stopwatch.StartNew();
        for (var round = 0; round < BuildRounds; round++)
        {
            var runs = await Task.WhenAll(Enumerable.Range(0, Builders).Select(_ => repo.FuseTimedAsync("build")));
            foreach (var run in runs)
            {
                walls.Add(run.Milliseconds);
                foreach (var line in CollisionLines(run.Output))
                {
                    collisions++;
                    if (samples.Count < 5)
                        samples.Add(line);
                }
            }
        }

        var wall = watch.Elapsed.TotalMilliseconds;
        Console.WriteLine($"[multiAgent] builds: {Builders} client(s) x {BuildRounds} rounds in {wall:0} ms, {collisions} collision(s)");
        return new MultiAgentBuilds(Builders, BuildRounds, wall, Stats(walls), collisions, samples);
    }

    /// <summary>
    ///     Three verify rounds. Round 1 edits a file in one project, round 2 edits a file in a different project and leaves
    ///     the first edit in place, round 3 edits the second again. A round that reruns a test project only the first edit
    ///     can reach is the waste the test cache is meant to remove.
    /// </summary>
    private static async Task<List<MultiAgentRound>> RoundsAsync(EvalRepo repo, SolutionInfo solution)
    {
        var byProject = solution.CodeSources()
            .Where(f => f.Contains("public", StringComparison.Ordinal))
            .GroupBy(f => solution.ProjectOf(f) ?? "", StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Project: g.Key, File: g.OrderBy(f => f, StringComparer.Ordinal).First()))
            .ToList();
        if (byProject.Count < 2)
            throw new InvalidOperationException($"the rounds scenario needs files in two different projects; found {byProject.Count}");

        var (p1, f1) = byProject[0];
        var (p2, f2) = byProject[1];
        var original1 = File.ReadAllText(f1);
        var original2 = File.ReadAllText(f2);
        var rounds = new List<MultiAgentRound>();
        var logSeen = repo.EngineLogLines().Count;
        try
        {
            var edits = new (string File, string Text, string Project)[]
            {
                (f1, TextEdit(original1, "1"), Path.GetFileName(p1)),
                (f2, TextEdit(original2, "1"), Path.GetFileName(p2)),
                (f2, TextEdit(original2, "2"), Path.GetFileName(p2)),
            };
            for (var i = 0; i < edits.Length; i++)
            {
                await File.WriteAllTextAsync(edits[i].File, edits[i].Text);
                var watch = Stopwatch.StartNew();
                var run = await repo.FuseTimedAsync("test");
                var ms = watch.Elapsed.TotalMilliseconds;
                var projects = TestProjectsSince(repo, ref logSeen);
                rounds.Add(new MultiAgentRound(i + 1, edits[i].Project, Path.GetRelativePath(repo.Root, edits[i].File), ms, projects, FailingCount(run.Output)));
                Console.WriteLine($"[multiAgent] round {i + 1}: edit in {edits[i].Project}, {ms:0} ms, {projects.Count} test project(s), {rounds[^1].FailingTests} failing");
            }

            return rounds;
        }
        finally
        {
            File.WriteAllText(f1, original1);
            File.WriteAllText(f2, original2);
        }
    }

    /// <summary>The test projects the engine planned since the last call, which it logs by name as it plans each one.</summary>
    private static List<string> TestProjectsSince(EvalRepo repo, ref int seen)
    {
        var lines = repo.EngineLogLines();
        var names = lines.Skip(seen).Select(m => TestPlanProject().Match(m)).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct().ToList();
        seen = lines.Count;
        return names;
    }

    private static int FailingCount(string output) =>
        int.TryParse(Failing().Match(output).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static IEnumerable<string> CollisionLines(string output) =>
        output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => CollisionIds.Any(id => l.Contains(id, StringComparison.Ordinal)));

    /// <summary>A body edit that leaves the declaration alone, so the check stays scoped to the one file.</summary>
    private static string BodyEdit(string original, int i) => TextEdit(original, i.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Adds a statement to the first method body in the file, or returns it unchanged when there is none.</summary>
    private static string TextEdit(string text, string i)
    {
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Body is { Statements.Count: > 0 });
        if (method?.Body is null)
            return text;

        var statement = SyntaxFactory.ParseStatement($"_ = {i};\n");
        return root.ReplaceNode(method.Body, method.Body.WithStatements(method.Body.Statements.Insert(0, statement))).ToFullString();
    }

    private static LatencyStats Stats(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        double At(double q) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(q * sorted.Count) - 1)];
        return new LatencyStats(Math.Round(At(0.5), 1), Math.Round(At(0.95), 1), Math.Round(sorted.LastOrDefault(), 1), sorted.Count);
    }

    [GeneratedRegex(@"^test plan: (.+?) \[", RegexOptions.CultureInvariant)]
    private static partial Regex TestPlanProject();

    [GeneratedRegex(@"fuse: (\d+) failed", RegexOptions.CultureInvariant)]
    private static partial Regex Failing();
}
