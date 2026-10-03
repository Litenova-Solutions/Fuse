using System.Diagnostics;
using System.Text.RegularExpressions;
using Fuse.Telemetry;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Evals;

/// <summary>
///     What four agents editing at once cost, and what one agent's check costs on its own. <c>GateByHolder</c> groups the
///     writers' gate waits by what held the request lock longest during each wait, so a long wait names what it was behind.
/// </summary>
internal sealed record MultiAgentWriters(
    int Clients,
    int EditsPerClient,
    double WallMs,
    int Checks,
    int Unanswered,
    int NotRun,
    LatencyStats Gate,
    Dictionary<string, LatencyStats> GateByHolder,
    LatencyStats EngineTotal,
    double SingleClientTotalP50);

/// <summary>What three builds at once cost, and how many of them collided on a file.</summary>
internal sealed record MultiAgentBuilds(int Clients, int Rounds, double WallMs, LatencyStats Each, int Collisions, List<string> CollisionSamples);

/// <summary>One verify round: the project the edit was in, how long the test run took, and the test projects it ran.</summary>
internal sealed record MultiAgentRound(int Index, string Project, string Edit, double Ms, List<string> TestProjects, int FailingTests);

/// <summary>
///     Two writers, each with a breaking change, whose checks reach each other's errors, checked once without sessions and
///     once as two sessions through the post-edit hook. "Own" errors are the ones a writer's change caused, "other" errors
///     the ones the other writer's change caused, both counted over the errors the answers print.
/// </summary>
/// <param name="RenamedIn">The file where writer A renamed a public method.</param>
/// <param name="Method">The method A renamed.</param>
/// <param name="BrokenIn">The file, in another project and calling the method, where writer B added a statement naming nothing.</param>
/// <param name="OwnWithoutSessions">Own errors in both writers' answers without sessions.</param>
/// <param name="OtherWithoutSessions">Other errors in both writers' answers without sessions: what a writer was told about the other's edit.</param>
/// <param name="OwnWithSessions">Own errors in both writers' answers as sessions; the same as without, or attribution lost one.</param>
/// <param name="OtherWithSessions">Other errors in both writers' answers as sessions.</param>
/// <param name="LeftToOtherSessions">The errors both answers as sessions counted as left to the other session.</param>
internal sealed record MultiAgentAttribution(
    string RenamedIn,
    string Method,
    string BrokenIn,
    int OwnWithoutSessions,
    int OtherWithoutSessions,
    int OwnWithSessions,
    int OtherWithSessions,
    int LeftToOtherSessions);

/// <summary>All four scenarios, as the latency result file's <c>multiAgent</c> object.</summary>
internal sealed record MultiAgentResult(MultiAgentWriters Writers, MultiAgentBuilds Builds, List<MultiAgentRound> Rounds, MultiAgentAttribution? Attribution);

/// <summary>
///     The four multi-agent scenarios: several agents checking at once, several builds at once, three verify rounds over
///     two projects, and two writers whose breaking changes reach each other. They answer whether a check queue is worth
///     coalescing, whether a test project is rerun for an edit that cannot reach it, and whether a writer is told about
///     another writer's errors.
/// </summary>
internal static partial class MultiAgentScenario
{
    private const int Writers = 4;
    private const int EditsPerWriter = 10;
    private const int Builders = 3;
    private const int BuildRounds = 5;

    /// <summary>The name writer B's statement uses, which nothing declares, so its error names it and no other error does.</summary>
    private const string MissingName = "fuseEvalMissingName";

    private static readonly string[] CollisionIds = ["MSB3021", "MSB3026", "MSB3027", "CS2012"];

    /// <summary>Runs the four scenarios against <paramref name="repo"/> and restores every file it edits.</summary>
    public static async Task<MultiAgentResult> RunAsync(EvalRepo repo, SolutionInfo solution, double singleClientTotalP50)
    {
        var targets = EditablePerProject(solution, text => TextEdit(text, "1")).Take(Writers).Select(t => t.File).ToList();
        var writers = await WritersAsync(repo, targets, singleClientTotalP50);
        var builds = await BuildsAsync(repo);
        var rounds = await RoundsAsync(repo, solution);
        var attribution = await AttributionAsync(repo, solution);
        return new MultiAgentResult(writers, builds, rounds, attribution);
    }

    /// <summary>
    ///     Writer A renames a public method that a file in another project calls, and writer B adds a statement naming an
    ///     undeclared variable to that calling file. A's check reaches B's file, and B's check of its own file sees A's
    ///     break there. Each writer is checked first with <c>fuse check</c>, which names no session, then through the
    ///     Claude Code post-edit hook with a session id each, B first so both are recorded before the answers that count.
    ///     Returns null when no method has a caller in another project whose break A's check reports.
    /// </summary>
    private static async Task<MultiAgentAttribution?> AttributionAsync(EvalRepo repo, SolutionInfo solution)
    {
        foreach (var (renamedIn, method, brokenIn) in AttributionCandidates(solution).Take(5))
        {
            var renamedBytes = File.ReadAllBytes(renamedIn);
            var brokenBytes = File.ReadAllBytes(brokenIn);
            try
            {
                var renamed = RenameMethod(File.ReadAllText(renamedIn), method);
                var broken = TextEdit(File.ReadAllText(brokenIn), MissingName);
                if (renamed is null || broken == File.ReadAllText(brokenIn))
                    continue;
                await File.WriteAllTextAsync(renamedIn, renamed);
                await File.WriteAllTextAsync(brokenIn, broken);
                var ownOfA = $"'{method}'";
                var relativeBroken = Path.GetRelativePath(repo.Root, brokenIn).Replace('\\', '/');

                var a = EvalRepo.ParseFuseErrors((await repo.FuseTimedAsync("check", renamedIn)).Output);
                // A candidate whose break A's check does not report in B's file measures nothing.
                if (!a.Any(e => e.StartsWith(relativeBroken + "(", StringComparison.Ordinal) && e.Contains(ownOfA, StringComparison.Ordinal)))
                    continue;
                var b = EvalRepo.ParseFuseErrors((await repo.FuseTimedAsync("check", brokenIn)).Output);

                await repo.FuseHookAsync("claude", "post-edit", HookPayload(repo, "eval-b", brokenIn));
                var sessionA = (await repo.FuseHookAsync("claude", "post-edit", HookPayload(repo, "eval-a", renamedIn))).Output;
                var sessionB = (await repo.FuseHookAsync("claude", "post-edit", HookPayload(repo, "eval-b", brokenIn))).Output;
                var aAsSession = EvalRepo.ParseFuseErrors(sessionA);
                var bAsSession = EvalRepo.ParseFuseErrors(sessionB);

                int Count(List<string> errors, string text) => errors.Count(e => e.Contains(text, StringComparison.Ordinal));
                var result = new MultiAgentAttribution(
                    Path.GetRelativePath(repo.Root, renamedIn).Replace('\\', '/'),
                    method,
                    relativeBroken,
                    Count(a, ownOfA) + Count(b, MissingName),
                    Count(a, MissingName) + Count(b, ownOfA),
                    Count(aAsSession, ownOfA) + Count(bAsSession, MissingName),
                    Count(aAsSession, MissingName) + Count(bAsSession, ownOfA),
                    LeftOut(sessionA) + LeftOut(sessionB));
                Console.WriteLine($"[multiAgent] attribution: {method} in {result.RenamedIn}, {MissingName} in {result.BrokenIn}; other writer's errors told {result.OtherWithoutSessions} without sessions, {result.OtherWithSessions} with; own {result.OwnWithoutSessions} and {result.OwnWithSessions}; {result.LeftToOtherSessions} left to the other session");
                return result;
            }
            finally
            {
                File.WriteAllBytes(renamedIn, renamedBytes);
                File.WriteAllBytes(brokenIn, brokenBytes);
            }
        }

        Console.WriteLine("[multiAgent] attribution: no public method has a caller in another project that its rename breaks");
        return null;
    }

    /// <summary>
    ///     Public methods with a block body, declared once in their file, in the code projects with the most dependents,
    ///     each with the first file of another code project that calls it by name.
    /// </summary>
    private static IEnumerable<(string RenamedIn, string Method, string BrokenIn)> AttributionCandidates(SolutionInfo solution)
    {
        var sources = solution.CodeSources();
        foreach (var project in solution.CodeProjects.OrderByDescending(solution.DependentCount).ThenBy(p => p, StringComparer.Ordinal).Take(2))
        {
            var dir = Path.GetDirectoryName(project)!;
            var own = sources.Where(f => string.Equals(solution.ProjectOf(f), dir, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).Take(200);
            var others = sources.Where(f => !string.Equals(solution.ProjectOf(f), dir, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
            foreach (var file in own)
            {
                var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
                var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
                foreach (var m in methods.Where(m => m.Body is { Statements.Count: > 0 } && m.Modifiers.Any(SyntaxKind.PublicKeyword) && !m.Modifiers.Any(SyntaxKind.OverrideKeyword) && m.Parent is ClassDeclarationSyntax))
                {
                    var name = m.Identifier.Text;
                    if (methods.Count(x => x.Identifier.Text == name) > 1)
                        continue;
                    var caller = others.FirstOrDefault(f => File.ReadAllText(f).Contains("." + name + "(", StringComparison.Ordinal));
                    if (caller is not null)
                        yield return (file, name, caller);
                }
            }
        }
    }

    /// <summary>The text with <paramref name="method"/>'s declaration renamed, or null when the file does not declare it.</summary>
    private static string? RenameMethod(string text, string method)
    {
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        var declaration = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == method);
        return declaration is null
            ? null
            : root.ReplaceToken(declaration.Identifier, SyntaxFactory.Identifier(method + "Renamed").WithTriviaFrom(declaration.Identifier)).ToFullString();
    }

    /// <summary>A Claude Code post-edit payload for <paramref name="file"/>, from <paramref name="session"/>.</summary>
    private static string HookPayload(EvalRepo repo, string session, string file) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["session_id"] = session,
            ["hook_event_name"] = "PostToolUse",
            ["tool_name"] = "Edit",
            ["cwd"] = repo.Root,
            ["tool_input"] = new Dictionary<string, string> { ["file_path"] = file },
        });

    /// <summary>The count in an answer's "N error(s) from other sessions' edits left out", or 0.</summary>
    private static int LeftOut(string output) =>
        int.TryParse(LeftToOthers().Match(output).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>
    ///     One file per non-test project that <paramref name="edit"/> would actually change, ordered by how many test
    ///     projects reference the project. A file the edit leaves alone would make the scenario measure nothing, and the
    ///     most-referenced projects are the ones an edit in them reaches, which is what the scenarios are about.
    /// </summary>
    private static List<(string Project, string File)> EditablePerProject(SolutionInfo solution, Func<string, string> edit)
    {
        var sources = solution.CodeSources();
        var result = new List<(string Project, string File)>();
        foreach (var project in solution.CodeProjects)
        {
            var dir = Path.GetDirectoryName(project)!;
            var file = sources
                .Where(f => string.Equals(solution.ProjectOf(f), dir, StringComparison.OrdinalIgnoreCase))
                .Select(f => (File: f, Text: File.ReadAllText(f)))
                .Where(x => !string.Equals(edit(x.Text), x.Text, StringComparison.Ordinal))
                .OrderBy(x => x.File, StringComparer.Ordinal)
                .Select(x => x.File)
                .FirstOrDefault();
            if (file is not null)
                result.Add((project, file));
        }

        return [.. result.OrderByDescending(x => solution.TestProjects.Count(t => File.ReadAllText(t).Contains(Path.GetFileName(x.Project), StringComparison.OrdinalIgnoreCase)))
            .ThenBy(x => x.Project, StringComparer.Ordinal)];
    }

    /// <summary>
    ///     Four clients, each editing a file in a different project ten times and checking it, all at once. A fifth
    ///     client acts as the verifier: it checks and runs the tests three times while the writers work. The gate waits in
    ///     the engine's own phase lines are what say whether a queue is forming.
    /// </summary>
    private static async Task<MultiAgentWriters> WritersAsync(EvalRepo repo, List<string> targets, double singleClientTotalP50)
    {
        // The bytes, not the text: a file with a byte order mark does not survive a text round trip, and the suite that
        // runs next on this repository needs a clean tree.
        var originals = targets.ToDictionary(f => f, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        var texts = originals.ToDictionary(p => p.Key, p => File.ReadAllText(p.Key), StringComparer.OrdinalIgnoreCase);
        var walls = new List<double>();
        var unanswered = 0;
        var notRun = 0;
        var gates = new List<double>();
        var gatesByHolder = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var totals = new List<double>();
        var checks = 0;

        var taken = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var watch = Stopwatch.StartNew();
            var clients = targets.Select(target => WriterAsync(repo, target, texts[target], taken, (ms, gate, gateHolder, total, exitCode) =>
            {
                lock (walls)
                {
                    walls.Add(ms);
                    unanswered += exitCode == 2 ? 1 : 0;
                    notRun += exitCode is 0 or 1 or 2 ? 0 : 1;
                    checks++;
                    if (gate is { } g)
                    {
                        gates.Add(g);
                        if (gateHolder is not null)
                        {
                            if (!gatesByHolder.TryGetValue(gateHolder, out var held))
                                gatesByHolder[gateHolder] = held = [];
                            held.Add(g);
                        }
                    }
                    if (total is { } t)
                        totals.Add(t);
                }
            })).ToList();
            clients.Add(VerifierAsync(repo));
            await Task.WhenAll(clients);
            var wall = watch.Elapsed.TotalMilliseconds;

            Console.WriteLine($"[multiAgent] writers: {targets.Count} client(s) x {EditsPerWriter} edits in {wall:0} ms, {checks} check(s), {unanswered} unanswered, {notRun} did not run");
            foreach (var (holder, waits) in gatesByHolder.OrderBy(h => h.Key, StringComparer.Ordinal))
                Console.WriteLine($"[multiAgent] writers: {waits.Count} gate wait(s) behind {holder}, longest {waits.Max():0} ms");
            return new MultiAgentWriters(
                targets.Count,
                EditsPerWriter,
                wall,
                checks,
                unanswered,
                notRun,
                LatencyStats.Of(gates),
                gatesByHolder.OrderBy(h => h.Key, StringComparer.Ordinal).ToDictionary(h => h.Key, h => LatencyStats.Of(h.Value), StringComparer.Ordinal),
                LatencyStats.Of(totals),
                singleClientTotalP50);
        }
        finally
        {
            foreach (var (file, bytes) in originals)
                File.WriteAllBytes(file, bytes);
        }
    }

    private static async Task WriterAsync(EvalRepo repo, string file, string original, HashSet<string> taken, Action<double, double?, string?, double?, int> report)
    {
        for (var i = 1; i <= EditsPerWriter; i++)
        {
            await File.WriteAllTextAsync(file, BodyEdit(original, i));
            // The client is timed the way an agent's call would be: a process start, a pipe, a request, an answer.
            var watch = Stopwatch.StartNew();
            var run = await repo.FuseTimedAsync("check", file);
            var ms = watch.Elapsed.TotalMilliseconds;
            var phases = RequestPhases.TakeNewest(repo.EngineLogLines(), taken);
            // A request with no gate phase did not wait for the gate at all; it is not a zero-length wait.
            double? gate = phases is not null && phases.Phases.TryGetValue(Phase.Gate, out var waited) ? waited : null;
            report(ms, gate, phases?.GateHolder, phases?.Total, run.ExitCode);
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
        return new MultiAgentBuilds(Builders, BuildRounds, wall, LatencyStats.Of(walls), collisions, samples);
    }

    /// <summary>
    ///     Three verify rounds. Round 1 edits a method body in a type that some test project uses, round 2 edits a body in a
    ///     different project whose tests are not all the same ones, and leaves the first edit in place, and round 3 edits
    ///     the second file again. Body edits keep every test compiling, so each round runs tests rather than reporting a
    ///     build error. A round that reruns a test project only the first edit reaches is the waste a test cache would
    ///     remove, so a pair of files that cannot show it fails the scenario rather than measuring nothing.
    /// </summary>
    private static async Task<List<MultiAgentRound>> RoundsAsync(EvalRepo repo, SolutionInfo solution)
    {
        var testSources = solution.TestProjects.ToDictionary(t => t, t => SourcesUnder(Path.GetDirectoryName(t)!).Select(File.ReadAllText).ToList(), StringComparer.OrdinalIgnoreCase);
        var candidates = EditablePerProject(solution, text => TextEdit(text, "1"))
            .Select(c => (c.Project, c.File, Tests: TestProjectsUsing(c.File, testSources)))
            .Where(c => c.Tests.Count > 0)
            .ToList();
        var pair = candidates.SelectMany(a => candidates.Where(b => b.Project != a.Project && a.Tests.Except(b.Tests).Any()).Select(b => (P1: a, P2: b))).FirstOrDefault();
        if (pair.P1.File is null)
        {
            // A repository with one test project, such as FluentValidation, has no pair whose tests differ. The rounds
            // still measure three test plans in a row, but cannot show a rerun of a test project only the first edit reaches.
            // Only projects a test project references count: a type a test merely names, such as a benchmark's, is not
            // something an edit reaches through a reference.
            var referenced = solution.TestProjects.SelectMany(SolutionInfo.ReferencesOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var reachable = candidates.Where(c => referenced.Contains(Path.GetFullPath(c.Project))).ToList();
            pair = reachable.SelectMany(a => reachable.Where(b => b.Project != a.Project).Select(b => (P1: a, P2: b))).FirstOrDefault();
            // With one such project, all three rounds edit its file.
            if (pair.P1.File is null && reachable.Count == 1)
                pair = (reachable[0], reachable[0]);
            if (pair.P1.File is null)
                throw new InvalidOperationException($"the rounds scenario needs two projects whose types a test project uses; found {candidates.Count} candidate(s) in {solution.Root}");
            Console.WriteLine("[multiAgent] rounds: every candidate pair reaches the same test projects, so the rounds cannot show a rerun");
        }

        var (p1, f1) = (pair.P1.Project, pair.P1.File);
        var (p2, f2) = (pair.P2.Project, pair.P2.File);
        Console.WriteLine($"[multiAgent] rounds: P1 {Path.GetRelativePath(repo.Root, f1)} (tests {string.Join(", ", pair.P1.Tests.Select(Path.GetFileNameWithoutExtension))}), P2 {Path.GetRelativePath(repo.Root, f2)} (tests {string.Join(", ", pair.P2.Tests.Select(Path.GetFileNameWithoutExtension))})");
        var bytes1 = File.ReadAllBytes(f1);
        var bytes2 = File.ReadAllBytes(f2);
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

            if (rounds[0].TestProjects.Count == 0)
                throw new InvalidOperationException($"round 1's edit in {Path.GetRelativePath(repo.Root, f1)} reached no test project, so the rounds cannot show a rerun");
            return rounds;
        }
        finally
        {
            File.WriteAllBytes(f1, bytes1);
            File.WriteAllBytes(f2, bytes2);
        }
    }

    /// <summary>The test projects whose source text names a type declared in <paramref name="file"/>.</summary>
    private static List<string> TestProjectsUsing(string file, Dictionary<string, List<string>> testSources)
    {
        var types = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .Select(t => t.Identifier.Text).Distinct(StringComparer.Ordinal).ToList();
        return [.. testSources.Where(t => t.Value.Any(text => types.Any(type => Regex.IsMatch(text, $@"\b{Regex.Escape(type)}\b")))).Select(t => t.Key).Order(StringComparer.Ordinal)];
    }

    private static List<string> SourcesUnder(string directory) =>
        [.. Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))];

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

    /// <summary>A body edit that leaves the declaration alone, so the check binds only that file.</summary>
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


    [GeneratedRegex(@"^test plan: (.+?) \[", RegexOptions.CultureInvariant)]
    private static partial Regex TestPlanProject();

    [GeneratedRegex(@"fuse: (\d+) failed", RegexOptions.CultureInvariant)]
    private static partial Regex Failing();

    [GeneratedRegex(@"(\d+) error\(s\) from other sessions' edits left out", RegexOptions.CultureInvariant)]
    private static partial Regex LeftToOthers();
}
