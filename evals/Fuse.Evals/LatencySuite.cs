using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Evals;

/// <summary>
///     Suite 7.3: times <c>fuse check</c> through the executable: a cold start, warm body edits, warm signature edits
///     that reach dependent projects, and warm checks of an unchanged tree (the client and sync overhead).
/// </summary>
internal static class LatencySuite
{
    public static async Task<object> RunAsync(EvalRepo repo, SolutionInfo solution, int bodyEdits = 15, int signatureEdits = 10)
    {
        if (!await repo.IsCleanAsync())
            throw new InvalidOperationException($"{repo.Root} has uncommitted changes; the suite needs a clean tree");
        var (file, method, references) = PickTarget(solution);
        Console.WriteLine($"[latency] {repo.Name}: target {Path.GetRelativePath(repo.Root, file)} method {method} ({references} referencing files in other projects)");
        var original = await File.ReadAllTextAsync(file);

        await repo.KillEngineAsync();
        var coldClean = await repo.FuseAsync("check");
        Console.WriteLine($"[latency] cold check of a clean tree: {coldClean.Milliseconds:0} ms ({Last(coldClean.Result.Output)})");

        await repo.KillEngineAsync();
        await File.WriteAllTextAsync(file, BodyEdit(original, method, 0));
        var coldEdit = await repo.FuseAsync("check", file);
        Console.WriteLine($"[latency] cold check of a body edit (loads the owning project): {coldEdit.Milliseconds:0} ms ({Last(coldEdit.Result.Output)})");

        var body = new List<double>();
        for (var i = 1; i <= bodyEdits; i++)
        {
            await File.WriteAllTextAsync(file, BodyEdit(original, method, i));
            var run = await repo.FuseAsync("check", file);
            body.Add(run.Milliseconds);
        }

        var signature = new List<double>();
        string? signatureSummary = null;
        for (var i = 1; i <= signatureEdits; i++)
        {
            await File.WriteAllTextAsync(file, SignatureEdit(original, method, i));
            var run = await repo.FuseAsync("check", file);
            signature.Add(run.Milliseconds);
            signatureSummary ??= Last(run.Result.Output);
        }

        await repo.ResetAsync();
        await Task.Delay(500);
        await repo.FuseAsync("check");
        var clean = new List<double>();
        for (var i = 0; i < 10; i++)
            clean.Add((await repo.FuseAsync("check")).Milliseconds);

        var engines = await repo.EngineProcessesAsync();
        var rss = engines.Count > 0 ? engines.Max(e => e.WorkingSet) / (1024.0 * 1024.0) : 0;
        var summary = new
        {
            suite = "latency",
            repo = repo.Name,
            target = Path.GetRelativePath(repo.Root, file).Replace('\\', '/'),
            method,
            referencingFilesInOtherProjects = references,
            coldCleanMs = coldClean.Milliseconds,
            coldBodyEditMs = coldEdit.Milliseconds,
            bodyEdit = Stats(body),
            signatureEdit = Stats(signature),
            signatureEditSample = signatureSummary,
            warmUnchanged = Stats(clean),
            engineWorkingSetMb = Math.Round(rss, 1),
            treeCleanAfter = await repo.IsCleanAsync(),
        };
        Console.WriteLine($"[latency] body edit P50 {summary.bodyEdit.P50:0} / P95 {summary.bodyEdit.P95:0} ms; signature edit P50 {summary.signatureEdit.P50:0} / P95 {summary.signatureEdit.P95:0} ms ({signatureSummary}); unchanged P50 {summary.warmUnchanged.P50:0} ms; engine {rss:0} MB");
        return summary;
    }

    private static string Last(string output) => output.Trim().Split('\n').Last().Trim();

    private static LatencyStats Stats(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        double At(double q) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(q * sorted.Count) - 1)];
        return new LatencyStats(Math.Round(At(0.5), 1), Math.Round(At(0.95), 1), Math.Round(sorted.LastOrDefault(), 1), sorted.Count);
    }

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
}

/// <summary>Percentiles of a series of wall times, in milliseconds.</summary>
internal sealed record LatencyStats(double P50, double P95, double Max, int N);
