using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Evals;

/// <summary>
///     One pair of writers, each with a breaking change, whose checks reach each other's errors, checked through the
///     Claude Code post-edit hook once without sessions and once as two sessions. "Own" errors are the ones a writer's
///     change caused, "other" errors the ones the other writer's change caused, both counted over the errors the answers print.
/// </summary>
/// <param name="RenamedIn">The file where writer A renamed a public method.</param>
/// <param name="Method">The method A renamed.</param>
/// <param name="BrokenIn">The file, in another project and calling the method, where writer B added a statement naming nothing.</param>
/// <param name="OwnWithoutSessions">Own errors in both writers' answers without sessions.</param>
/// <param name="OtherWithoutSessions">Other errors in both writers' answers without sessions: what a writer was told about the other's edit.</param>
/// <param name="OwnWithSessions">Own errors in both writers' answers as sessions; the same as without, or attribution lost one.</param>
/// <param name="OtherWithSessions">Other errors in both writers' answers as sessions.</param>
/// <param name="LeftToOtherSessions">The errors both answers as sessions counted as left to the other session.</param>
/// <param name="MsWithoutSessions">Wall time of both writers' hooks without sessions, in milliseconds.</param>
/// <param name="MsWithSessions">Wall time of both writers' hooks as sessions, in milliseconds, which includes the attribution.</param>
internal sealed record AttributionPair(
    string RenamedIn,
    string Method,
    string BrokenIn,
    int OwnWithoutSessions,
    int OtherWithoutSessions,
    int OwnWithSessions,
    int OtherWithSessions,
    int LeftToOtherSessions,
    double MsWithoutSessions,
    double MsWithSessions);

/// <summary>
///     Whether a writer that shares a working tree with another is told the other's errors, with and without sessions,
///     and what the attribution costs. Writer A renames a public method that a file in another project calls, and writer B
///     adds a statement naming an undeclared variable to that calling file, so A's check reaches B's file and B's check of
///     its own file sees A's break there. It is the light way to measure attribution on a repository: no truth, no test runs.
/// </summary>
internal static partial class AttributionSuite
{
    /// <summary>How many pairs the suite measures on a repository.</summary>
    private const int Pairs = 3;

    /// <summary>The name writer B's statement uses, which nothing declares, so its error names it and no other error does.</summary>
    private const string MissingName = "fuseEvalMissingName";

    /// <summary>Measures up to three pairs on <paramref name="repo"/> and returns the result file's content.</summary>
    public static async Task<object> RunAsync(EvalRepo repo, SolutionInfo solution)
    {
        if (!await repo.IsCleanAsync())
            throw new InvalidOperationException($"{repo.Root} has uncommitted changes; the suite needs a clean tree");
        var pairs = await PairsAsync(repo, solution, Pairs);
        if (pairs.Count == 0)
            throw new InvalidOperationException($"no public method in {repo.Root} has a caller in another project whose break the check reports");
        var summary = new
        {
            suite = "attribution",
            repo = repo.Name,
            commit = await repo.HeadAsync(),
            fuseBuild = await repo.VersionAsync(),
            pairs,
            ownWithoutSessions = pairs.Sum(p => p.OwnWithoutSessions),
            otherWithoutSessions = pairs.Sum(p => p.OtherWithoutSessions),
            ownWithSessions = pairs.Sum(p => p.OwnWithSessions),
            otherWithSessions = pairs.Sum(p => p.OtherWithSessions),
            leftToOtherSessions = pairs.Sum(p => p.LeftToOtherSessions),
            msWithoutSessions = LatencyStats.Of([.. pairs.Select(p => p.MsWithoutSessions)]),
            msWithSessions = LatencyStats.Of([.. pairs.Select(p => p.MsWithSessions)]),
            treeCleanAfter = await repo.IsCleanAsync(),
        };
        Console.WriteLine($"[attribution] {pairs.Count} pair(s): other writer's errors told {summary.otherWithoutSessions} without sessions, {summary.otherWithSessions} with; own {summary.ownWithoutSessions} and {summary.ownWithSessions}; {summary.leftToOtherSessions} left to the other session");
        return summary;
    }

    /// <summary>
    ///     Measures up to <paramref name="count"/> pairs, each with a different method and a different calling file,
    ///     restoring every file it edits. A candidate whose break A's check does not report in B's file measures nothing
    ///     and is skipped.
    /// </summary>
    public static async Task<List<AttributionPair>> PairsAsync(EvalRepo repo, SolutionInfo solution, int count)
    {
        var result = new List<AttributionPair>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (renamedIn, method, brokenIn) in Candidates(solution).Where(c => !used.Contains(c.BrokenIn)).Take(count * 4))
        {
            if (result.Count == count)
                break;
            if (used.Contains(brokenIn))
                continue;
            var renamedBytes = File.ReadAllBytes(renamedIn);
            var brokenBytes = File.ReadAllBytes(brokenIn);
            try
            {
                var renamed = RenameMethod(File.ReadAllText(renamedIn), method);
                var broken = MultiAgentScenario.TextEdit(File.ReadAllText(brokenIn), MissingName);
                if (renamed is null || broken == File.ReadAllText(brokenIn))
                    continue;
                await File.WriteAllTextAsync(renamedIn, renamed);
                await File.WriteAllTextAsync(brokenIn, broken);
                if (await MeasureAsync(repo, renamedIn, method, brokenIn, result.Count + 1) is { } pair)
                {
                    result.Add(pair);
                    used.Add(brokenIn);
                    Console.WriteLine($"[attribution] {method} in {pair.RenamedIn}, {MissingName} in {pair.BrokenIn}: other {pair.OtherWithoutSessions} to {pair.OtherWithSessions}, own {pair.OwnWithoutSessions} to {pair.OwnWithSessions}, {pair.MsWithoutSessions:0} ms to {pair.MsWithSessions:0} ms");
                }
            }
            finally
            {
                File.WriteAllBytes(renamedIn, renamedBytes);
                File.WriteAllBytes(brokenIn, brokenBytes);
                // A check of the restored tree lets the engine forget the sessions that wrote these files.
                await repo.FuseTimedAsync("check");
            }
        }

        if (result.Count == 0)
            Console.WriteLine("[attribution] no public method has a caller in another project whose break the check reports");
        return result;
    }

    private static async Task<AttributionPair?> MeasureAsync(EvalRepo repo, string renamedIn, string method, string brokenIn, int index)
    {
        var ownOfA = $"'{method}'";
        var relativeBroken = Path.GetRelativePath(repo.Root, brokenIn).Replace('\\', '/');

        // Unmeasured checks first, so both arms find the projects loaded; the first also says whether the pair measures anything.
        var reach = EvalRepo.ParseFuseErrors((await repo.FuseTimedAsync("check", renamedIn)).Output);
        if (!reach.Any(e => e.StartsWith(relativeBroken + "(", StringComparison.Ordinal) && e.Contains(ownOfA, StringComparison.Ordinal)))
            return null;
        await repo.FuseTimedAsync("check", brokenIn);

        var (a, aMs) = await HookAsync(repo, null, renamedIn);
        var (b, bMs) = await HookAsync(repo, null, brokenIn);

        // B first, so both sessions are recorded before the answers that count.
        var (sessionA, sessionB) = ($"eval-a-{index}", $"eval-b-{index}");
        await HookAsync(repo, sessionB, brokenIn);
        var (aAsSession, aSessionMs) = await HookAsync(repo, sessionA, renamedIn);
        var (bAsSession, bSessionMs) = await HookAsync(repo, sessionB, brokenIn);

        int Count(string output, string text) => EvalRepo.ParseFuseErrors(output).Count(e => e.Contains(text, StringComparison.Ordinal));
        return new AttributionPair(
            Path.GetRelativePath(repo.Root, renamedIn).Replace('\\', '/'),
            method,
            relativeBroken,
            Count(a, ownOfA) + Count(b, MissingName),
            Count(a, MissingName) + Count(b, ownOfA),
            Count(aAsSession, ownOfA) + Count(bAsSession, MissingName),
            Count(aAsSession, MissingName) + Count(bAsSession, ownOfA),
            LeftOut(aAsSession) + LeftOut(bAsSession),
            aMs + bMs,
            aSessionMs + bSessionMs);
    }

    /// <summary>Runs the Claude Code post-edit hook for <paramref name="file"/>, from <paramref name="session"/> or from no session.</summary>
    private static async Task<(string Output, double Ms)> HookAsync(EvalRepo repo, string? session, string file)
    {
        var payload = new Dictionary<string, object>
        {
            ["hook_event_name"] = "PostToolUse",
            ["tool_name"] = "Edit",
            ["cwd"] = repo.Root,
            ["tool_input"] = new Dictionary<string, string> { ["file_path"] = file },
        };
        if (session is not null)
            payload["session_id"] = session;
        var watch = Stopwatch.StartNew();
        var result = await repo.FuseHookAsync("claude", "post-edit", System.Text.Json.JsonSerializer.Serialize(payload));
        return (result.Output, watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    ///     Public methods with a block body, declared once in their file, in the code projects with the most dependents,
    ///     each with up to three files of other code projects that call it by name.
    /// </summary>
    private static IEnumerable<(string RenamedIn, string Method, string BrokenIn)> Candidates(SolutionInfo solution)
    {
        var sources = solution.CodeSources();
        foreach (var project in solution.CodeProjects.OrderByDescending(solution.DependentCount).ThenBy(p => p, StringComparer.Ordinal).Take(2))
        {
            var dir = Path.GetDirectoryName(project)!;
            var own = sources.Where(f => string.Equals(solution.ProjectOf(f), dir, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).Take(200);
            var others = sources.Where(f => !string.Equals(solution.ProjectOf(f), dir, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)
                .Select(f => (File: f, Text: File.ReadAllText(f))).ToList();
            foreach (var file in own)
            {
                var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
                var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
                foreach (var m in methods.Where(m => m.Body is { Statements.Count: > 0 } && m.Modifiers.Any(SyntaxKind.PublicKeyword) && !m.Modifiers.Any(SyntaxKind.OverrideKeyword) && m.Parent is ClassDeclarationSyntax))
                {
                    var name = m.Identifier.Text;
                    if (methods.Count(x => x.Identifier.Text == name) > 1)
                        continue;
                    foreach (var caller in others.Where(f => f.Text.Contains("." + name + "(", StringComparison.Ordinal)).Take(3))
                        yield return (file, name, caller.File);
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

    /// <summary>The count in an answer's "N error(s) from other sessions' edits left out", or 0.</summary>
    private static int LeftOut(string output) =>
        int.TryParse(LeftToOthers().Match(output).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;

    [GeneratedRegex(@"(\d+) error\(s\) from other sessions' edits left out", RegexOptions.CultureInvariant)]
    private static partial Regex LeftToOthers();
}
