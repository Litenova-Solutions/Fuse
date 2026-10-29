using Fuse.Telemetry;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Evals;

/// <summary>One edit that changes no compiled code: what a check and a test round cost for it.</summary>
internal sealed record NoCodeEdit(
    string Kind,
    double? CheckMs,
    int? FilesChecked,
    int? CheckErrors,
    double? TestMs,
    int? TestsSelected,
    int? TestsRun,
    bool? NoCodeChange,
    string? Note);

/// <summary>
///     Times <c>fuse check</c> through the executable: a cold start, warm body edits, warm signature edits
///     that reach dependent projects, and warm checks of an unchanged tree (the client and sync overhead).
/// </summary>
internal static class LatencySuite
{
    private const int TestRounds = 5;

    /// <param name="part">
    ///     What to measure, so one repository's suite can run on several machines at once: <c>all</c>; <c>checks</c>, the
    ///     checks alone, which the managed client's run uses, since the client's cost shows in the checks; <c>main</c>, the
    ///     checks and the test rounds; or <c>agents</c>, the multi-agent scenario, after the cold checks and the body edits
    ///     it is compared with. A part leaves what it does not measure null.
    /// </param>
    public static async Task<object> RunAsync(EvalRepo repo, SolutionInfo solution, string part = "all", int bodyEdits = 15, int signatureEdits = 10)
    {
        if (!await repo.IsCleanAsync())
            throw new InvalidOperationException($"{repo.Root} has uncommitted changes; the suite needs a clean tree");
        var (file, method, references) = PickTarget(solution);
        Console.WriteLine($"[latency] {repo.Name}: target {Path.GetRelativePath(repo.Root, file)} method {method} ({references} referencing files in other projects)");
        if (part is not ("all" or "checks" or "main" or "agents"))
            throw new ArgumentException($"unknown latency part {part}; use all, checks, main or agents");
        var signatures = part != "agents";
        var tests = part is "all" or "main";
        var agents = part is "all" or "agents";
        var original = await File.ReadAllTextAsync(file);
        // The bytes are kept as well: a file with a byte order mark does not survive a text round trip, and the next suite
        // to run on this repository needs a clean tree.
        var originalBytes = await File.ReadAllBytesAsync(file);

        // Every timed call takes the engine's own account of the request it made, so the report can say where the time went.
        var taken = new HashSet<string>(StringComparer.Ordinal);
        async Task<TimedCall> Timed(FuseRun run) => new(run.Milliseconds, RequestPhases.TakeNewest(repo.EngineLogLines(), taken));

        // A repository an agent works in has been built, and a test run reuses its build output, so the suite builds HEAD
        // first; on a fresh clone the first test rounds would otherwise time the whole first build.
        var head = await repo.BuildAsync();
        if (head.ExitCode != 0)
            throw new InvalidOperationException($"HEAD does not build: {string.Join("; ", head.Errors.Take(3))}");

        await repo.KillEngineAsync();
        var coldClean = await repo.FuseTimedAsync("check");
        Console.WriteLine($"[latency] cold check of a clean tree: {coldClean.Milliseconds:0} ms ({Last(coldClean.Output)})");

        await repo.KillEngineAsync();
        await File.WriteAllTextAsync(file, BodyEdit(original, method, 0));
        var coldEdit = await repo.FuseTimedAsync("check", file);
        Console.WriteLine($"[latency] cold check of a body edit (loads the owning project): {coldEdit.Milliseconds:0} ms ({Last(coldEdit.Output)})");

        var body = new List<double>();
        var bodyCalls = new List<TimedCall>();
        for (var i = 1; i <= bodyEdits; i++)
        {
            await File.WriteAllTextAsync(file, BodyEdit(original, method, i));
            var run = await repo.FuseTimedAsync("check", file);
            body.Add(run.Milliseconds);
            bodyCalls.Add(await Timed(run));
        }

        // Five edits that change no compiled code, each on the pristine target on its own, measured as a check and, where
        // the part runs tests, as a test round: what Fuse avoids for them.
        var noCodeEdits = new List<NoCodeEdit>();
        var noCodeCheckMs = new List<double>();
        var noCodeCheckCalls = new List<TimedCall>();
        var noCodeTestMs = new List<double>();
        var noCodeTestCalls = new List<TimedCall>();
        foreach (var (kind, changed, skipped) in NoCodeEdits(original, method))
        {
            if (changed is null)
            {
                noCodeEdits.Add(new NoCodeEdit(kind, null, null, null, null, null, null, null, skipped));
                continue;
            }

            await File.WriteAllTextAsync(file, changed);
            await Task.Delay(500);
            var logStart = repo.EngineLogLines().Count;
            var check = await repo.FuseTimedAsync("check", file);
            var timedCheck = await Timed(check);
            noCodeCheckMs.Add(check.Milliseconds);
            noCodeCheckCalls.Add(timedCheck);
            var checkCounts = CheckCounts.Parse(check.Output, repo.EngineLogLines().Skip(logStart).ToList(), timedCheck.Phases);
            double? testMs = null;
            int? testsSelected = null;
            int? testsRun = null;
            bool? noCode = null;
            if (tests)
            {
                var test = await repo.FuseTimedAsync("test");
                testMs = test.Milliseconds;
                noCodeTestMs.Add(test.Milliseconds);
                noCodeTestCalls.Add(await Timed(test));
                testsSelected = TestPlanCounts.SelectedTests(test.Output);
                var testCounts = TestPlanCounts.RunCounts(test.Output);
                testsRun = testCounts.Failed + testCounts.Passed;
                noCode = TestPlanCounts.CompilesToSameCode(test.Output);
            }

            noCodeEdits.Add(new NoCodeEdit(kind, check.Milliseconds, checkCounts.FilesChecked, EvalRepo.ParseFuseCount(check.Output), testMs, testsSelected, testsRun, noCode, null));
            await File.WriteAllBytesAsync(file, originalBytes);
        }

        var signature = new List<double>();
        var signatureCalls = new List<TimedCall>();
        string? signatureSummary = null;
        for (var i = 1; signatures && i <= signatureEdits; i++)
        {
            await File.WriteAllTextAsync(file, SignatureEdit(original, method, i));
            var run = await repo.FuseTimedAsync("check", file);
            signature.Add(run.Milliseconds);
            signatureCalls.Add(await Timed(run));
            signatureSummary ??= Last(run.Output);
        }

        await repo.ResetAsync();
        await Task.Delay(500);
        await repo.FuseAsync("check");
        var clean = new List<double>();
        var cleanCalls = new List<TimedCall>();
        for (var i = 0; signatures && i < 10; i++)
        {
            var run = await repo.FuseTimedAsync("check");
            clean.Add(run.Milliseconds);
            cleanCalls.Add(await Timed(run));
        }

        // A test round is what an agent waits for when it runs the tests, so it is measured the same way.
        var testRound = new List<double>();
        var testCalls = new List<TimedCall>();
        for (var i = 1; tests && i <= TestRounds; i++)
        {
            await File.WriteAllTextAsync(file, BodyEdit(original, method, 100 + i));
            var run = await repo.FuseTimedAsync("test");
            testRound.Add(run.Milliseconds);
            testCalls.Add(await Timed(run));
        }

        await File.WriteAllBytesAsync(file, originalBytes);
        var engines = await repo.EngineProcessesAsync();
        var rss = engines.Count > 0 ? engines.Max(e => e.WorkingSet) / (1024.0 * 1024.0) : 0;
        // The engine started for the cold body edit served every later check and test round, so its peak is theirs.
        var peak = engines.Count > 0 ? engines.Max(e => e.PeakWorkingSet) / (1024.0 * 1024.0) : 0;
        // An engine returns the memory its requests freed once idle for 30 s, and looks every 15 s, so 50 s covers both.
        await Task.Delay(TimeSpan.FromSeconds(50));
        var idleEngines = await repo.EngineProcessesAsync();
        var idle = idleEngines.Count > 0 ? idleEngines.Max(e => e.WorkingSet) / (1024.0 * 1024.0) : 0;
        var bodyPhases = PhaseReport.Build(bodyCalls);
        var signaturePhases = PhaseReport.Build(signatureCalls);
        var cleanPhases = PhaseReport.Build(cleanCalls);
        var testPhases = PhaseReport.Build(testCalls);
        if (part == "agents")
        {
            // In a whole run the test rounds come first and leave the test projects loaded, so the agents part runs one
            // unmeasured test round to start from the same engine.
            await File.WriteAllTextAsync(file, BodyEdit(original, method, 100));
            await repo.FuseAsync("test");
            await File.WriteAllBytesAsync(file, originalBytes);
        }

        // The writers scenario compares the queue against one client on its own, which is the body-edit total above.
        var multiAgent = !agents ? null : await MultiAgentScenario.RunAsync(repo, solution, bodyPhases.Phases.GetValueOrDefault(Phase.Total)?.P50 ?? 0);
        var summary = new
        {
            suite = "latency",
            repo = repo.Name,
            commit = await repo.HeadAsync(),
            fuseBuild = await repo.VersionAsync(),
            target = Path.GetRelativePath(repo.Root, file).Replace('\\', '/'),
            method,
            referencingFilesInOtherProjects = references,
            coldCleanMs = coldClean.Milliseconds,
            coldBodyEditMs = coldEdit.Milliseconds,
            bodyEdit = LatencyStats.Of(body),
            signatureEdit = signatures ? LatencyStats.Of(signature) : null,
            signatureEditSample = signatureSummary,
            warmUnchanged = signatures ? LatencyStats.Of(clean) : null,
            part,
            testRound = tests ? LatencyStats.Of(testRound) : null,
            bodyEditPhases = bodyPhases,
            signatureEditPhases = signatures ? signaturePhases : null,
            warmUnchangedPhases = signatures ? cleanPhases : null,
            testRoundPhases = tests ? testPhases : null,
            noCodeChange = new
            {
                edits = noCodeEdits,
                check = LatencyStats.Of(noCodeCheckMs),
                testRound = tests ? LatencyStats.Of(noCodeTestMs) : null,
                checkPhases = PhaseReport.Build(noCodeCheckCalls),
                testRoundPhases = tests ? PhaseReport.Build(noCodeTestCalls) : null,
            },
            multiAgent,
            engineWorkingSetMb = Math.Round(rss, 1),
            enginePeakWorkingSetMb = Math.Round(peak, 1),
            engineIdleWorkingSetMb = Math.Round(idle, 1),
            treeCleanAfter = await repo.IsCleanAsync(),
        };
        Console.WriteLine($"[latency] body edit P50 {summary.bodyEdit.P50:0} / P95 {summary.bodyEdit.P95:0} ms; signature edit P50 {summary.signatureEdit?.P50:0} / P95 {summary.signatureEdit?.P95:0} ms ({signatureSummary}); unchanged P50 {summary.warmUnchanged?.P50:0} ms; test round P50 {summary.testRound?.P50:0} ms; engine {rss:0} MB after the last request, {peak:0} MB at peak, {idle:0} MB once idle");
        Console.WriteLine($"[latency] body edit phases: {PhaseReport.Describe(summary.bodyEditPhases)}");
        if (summary.testRoundPhases is { } testRoundPhases)
            Console.WriteLine($"[latency] test round phases: {PhaseReport.Describe(testRoundPhases)}");
        Console.WriteLine($"[latency] no-code edits: {noCodeEdits.Count(e => e.NoCodeChange == true)} of {noCodeEdits.Count(e => e.CheckMs is not null)} detected as the same code, {noCodeEdits.Sum(e => e.TestsRun ?? 0)} test(s) run");
        return summary;
    }

    private static string Last(string output) => output.Trim().Split('\n').Last().Trim();


    /// <summary>A public method with a block body, in the code project with the most dependents, whose name other projects mention most.</summary>
    private static (string File, string Method, int References) PickTarget(SolutionInfo solution)
    {
        var otherFiles = solution.Projects.Select(p => Path.GetDirectoryName(p)!)
            .ToDictionary(d => d, d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)).ToList());
        var best = (File: "", Method: "", References: -1);
        foreach (var project in solution.CodeProjects.OrderByDescending(solution.DependentCount).Take(2))
        {
            var dir = Path.GetDirectoryName(project)!;
            foreach (var file in otherFiles[dir].Take(400))
            {
                var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
                foreach (var m in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Body is { Statements.Count: > 0 } && m.Modifiers.Any(SyntaxKind.PublicKeyword) && m.Parent is ClassDeclarationSyntax && !m.Modifiers.Any(SyntaxKind.OverrideKeyword)).Take(5))
                {
                    var name = m.Identifier.Text;
                    if (root.DescendantNodes().OfType<MethodDeclarationSyntax>().Count(x => x.Identifier.Text == name) > 1)
                        continue;
                    var refs = otherFiles.Where(kv => kv.Key != dir).Sum(kv => kv.Value.Count(f => File.ReadAllText(f).Contains(name + "(", StringComparison.Ordinal)));
                    if (refs > best.References)
                        best = (file, name, refs);
                }
            }
        }

        if (best.References < 0)
            throw new InvalidOperationException("no public method with a block body found");
        return best;
    }

    private static MethodDeclarationSyntax Find(SyntaxNode root, string method) =>
        root.DescendantNodes().OfType<MethodDeclarationSyntax>().First(m => m.Identifier.Text == method && m.Body is not null);

    private static string BodyEdit(string text, string method, int i)
    {
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        var m = Find(root, method);
        var statement = SyntaxFactory.ParseStatement($"_ = {i};\n");
        return root.ReplaceNode(m.Body!, m.Body!.WithStatements(m.Body.Statements.Insert(0, statement))).ToFullString();
    }

    private static string SignatureEdit(string text, string method, int i)
    {
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        var m = Find(root, method);
        return root.ReplaceToken(m.Identifier, SyntaxFactory.Identifier(method + "Renamed" + i).WithTriviaFrom(m.Identifier)).ToFullString();
    }

    /// <summary>
    ///     The five edits that change no compiled code: a comment, a local rename, a reformat, a using sort and whitespace.
    ///     An edit the target does not allow comes back null with the reason, so the suite measures the rest instead of
    ///     failing the repository.
    /// </summary>
    private static List<(string Kind, string? Text, string? Skipped)> NoCodeEdits(string original, string method) =>
    [
        ("comment", "// fuse evals: a comment changes no compiled code\n" + original, null),
        ("local-rename", LocalRenameEdit(original), "no method in the target file declares a local variable"),
        ("reformat", ReformatEdit(original, method), "the target method has no indented line"),
        ("using-sort", UsingSortEdit(original), "the target file has fewer than two using directives"),
        ("whitespace", WhitespaceEdit(original), null),
    ];

    /// <summary>Renames a local variable in the first method that declares one, or null when no method does.</summary>
    private static string? LocalRenameEdit(string text)
    {
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var declarator = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .FirstOrDefault(v => v.Parent is VariableDeclarationSyntax { Parent: LocalDeclarationStatementSyntax });
            if (declarator is null)
                continue;
            var name = declarator.Identifier.Text;
            var renamed = name + "Evals";
            // Only a name that stays unique in the method is renamed, so the edit still compiles.
            if (method.DescendantTokens().Any(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == renamed))
                continue;
            return root.ReplaceTokens(
                method.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == name),
                (token, _) => SyntaxFactory.Identifier(renamed).WithTriviaFrom(token)).ToFullString();
        }

        return null;
    }

    /// <summary>Indents one line of <paramref name="method"/> four more spaces, or null when it has no indented line.</summary>
    private static string? ReformatEdit(string text, string method)
    {
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        var declaration = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == method && m.Body is not null);
        if (declaration?.Body is null)
            return null;
        var body = text.Substring(declaration.Body.Span.Start, declaration.Body.Span.Length);
        var newline = body.IndexOf('\n');
        while (newline >= 0)
        {
            var spaces = 0;
            while (newline + 1 + spaces < body.Length && body[newline + 1 + spaces] == ' ')
                spaces++;
            if (spaces > 0)
                return text.Insert(declaration.Body.Span.Start + newline + 1, "    ");
            newline = body.IndexOf('\n', newline + 1);
        }

        return null;
    }

    /// <summary>Swaps the first two using directives with the same parent, or null when there are fewer than two.</summary>
    private static string? UsingSortEdit(string text)
    {
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>().ToList();
        var first = usings.FirstOrDefault();
        var second = first is null ? null : usings.FirstOrDefault(u => u != first && u.Parent == first.Parent);
        if (first is null || second is null)
            return null;
        return root.ReplaceNodes(new[] { first, second }, (node, _) => node == first ? second.WithTriviaFrom(first) : first.WithTriviaFrom(second)).ToFullString();
    }

    /// <summary>Trails three spaces after the first non-empty line.</summary>
    private static string WhitespaceEdit(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length > 0)
            {
                lines[i] = lines[i].TrimEnd('\r') + "   ";
                break;
            }
        }

        return string.Join('\n', lines);
    }
}
