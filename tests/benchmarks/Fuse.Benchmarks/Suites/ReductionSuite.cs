using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.ML.Tokenizers;

namespace Fuse.Benchmarks;

/// <summary>
///     The offline continuity layer: token reduction and public-API fidelity. For each target it counts the
///     raw o200k_base tokens of the C# files, reduces them at each level through the shipped reduction path,
///     counts the reduced tokens, and (at skeleton level) checks how many public and protected types and
///     methods survive the reduction. Deterministic and offline; it needs no MSBuild load and no model.
/// </summary>
/// <remarks>
///     Fidelity uses presence keys that survive reduction (a type by name, a method by <c>Name(</c> signature),
///     parsed from the raw source with Roslyn as an independent ground truth so the measure is not circular.
///     Reduction is performed through a caller-supplied delegate so the suite stays in the Core-only project
///     while the <c>fuse eval</c> command supplies the real reduction pipeline.
/// </remarks>
public sealed class ReductionSuite : IEvalSuite
{
    private static readonly string[] Levels = ["standard", "aggressive", "skeleton", "publicApi"];
    private readonly Func<string, IReadOnlyList<string>, string, CancellationToken, Task<string>> _reduce;
    private readonly TiktokenTokenizer _tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

    /// <summary>
    ///     Initializes a new instance of the <see cref="ReductionSuite" /> class.
    /// </summary>
    /// <param name="reduce">
    ///     Reduces a list of files (relative to a base directory) at a named level and returns the fused output.
    ///     Supplied by the <c>fuse eval</c> command, which wires it to the reduction pipeline.
    /// </param>
    public ReductionSuite(Func<string, IReadOnlyList<string>, string, CancellationToken, Task<string>> reduce)
        => _reduce = reduce;

    /// <inheritdoc />
    public string Name => "reduce";

    /// <inheritdoc />
    public string Description => "Token reduction and public-API fidelity over the corpus (offline continuity layer).";

    /// <inheritdoc />
    public async Task<SuiteResult> RunAsync(EvalOptions options, CancellationToken cancellationToken)
    {
        var targets = ResolveTargets(options);
        if (targets.Count == 0)
            return new SuiteResult(Name, Description, null, new Scorecard(0, 0, 0, 0, 0, 0, 0, 0), [],
                ["No corpus repositories or fixtures found; skipped."]);

        var tasks = new List<TaskResult>();
        var notes = new List<string>();
        foreach (var (name, dir) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            options.Report($"reduce: measuring {name}");
            var result = await MeasureAsync(name, dir, notes, cancellationToken);
            if (result is not null)
                tasks.Add(result);
        }

        if (tasks.Count == 0)
            return new SuiteResult(Name, Description, null, new Scorecard(0, 0, 0, 0, 0, 0, 0, 0), [], ["No target produced a measurement."]);

        var fidelity = tasks.Select(t => t.Recall).ToList();
        var (ciLow, ciHigh) = Metrics.BootstrapCi(fidelity);
        return new SuiteResult(Name, Description, null,
            new Scorecard(
                tasks.Count,
                Metrics.Mean(fidelity), ciLow, ciHigh,
                Metrics.Mean(tasks.Select(t => t.Precision).ToList()),
                0,
                Metrics.Median(tasks.Select(t => (double)t.Tokens)),
                Metrics.Mean(tasks.Select(t => (double)t.Tokens).ToList())),
            tasks, notes);
    }

    private async Task<TaskResult?> MeasureAsync(string name, string dir, List<string> notes, CancellationToken ct)
    {
        var csFiles = EnumerateCsFiles(dir);
        if (csFiles.Count == 0)
            return null;

        long rawTokens = 0;
        var truthTypes = new HashSet<string>(StringComparer.Ordinal);
        var truthMethods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in csFiles)
        {
            var text = await File.ReadAllTextAsync(Path.Combine(dir, file), ct);
            rawTokens += _tokenizer.CountTokens(text);
            CollectPublicApi(text, truthTypes, truthMethods);
        }

        // Reduction percentage per level (reduced output tokens vs raw tokens).
        var reductionByLevel = new Dictionary<string, double>(StringComparer.Ordinal);
        var skeletonOutput = string.Empty;
        var skeletonTokens = 0;
        foreach (var level in Levels)
        {
            var output = await _reduce(dir, csFiles, level, ct);
            if (output.StartsWith("Error", StringComparison.Ordinal))
            {
                notes.Add($"{name}: reduce at {level} failed: {output}");
                continue;
            }

            var reduced = _tokenizer.CountTokens(output);
            reductionByLevel[level] = rawTokens == 0 ? 0 : 1.0 - (double)reduced / rawTokens;
            if (level == "skeleton")
            {
                skeletonOutput = output;
                skeletonTokens = reduced;
            }
        }

        // Fidelity at skeleton: how many public types and methods survive (presence keys that survive reduction).
        var keptTypes = truthTypes.Count(t => skeletonOutput.Contains(t, StringComparison.Ordinal));
        var keptMethods = truthMethods.Count(m => skeletonOutput.Contains(m + "(", StringComparison.Ordinal));
        var totalApi = truthTypes.Count + truthMethods.Count;
        var keptApi = keptTypes + keptMethods;
        var fidelity = totalApi == 0 ? 1.0 : (double)keptApi / totalApi;

        var levelSummary = string.Join(", ", Levels
            .Where(reductionByLevel.ContainsKey)
            .Select(l => $"{l} {reductionByLevel[l]:P0}"));
        notes.Add($"{name}: {csFiles.Count} files, raw {rawTokens:N0} tokens; reduction by level: {levelSummary}; " +
                  $"skeleton fidelity types {keptTypes}/{truthTypes.Count}, methods {keptMethods}/{truthMethods.Count}");

        return new TaskResult(
            name, name, "reduction",
            fidelity,
            reductionByLevel.GetValueOrDefault("skeleton"),
            skeletonTokens, 0,
            new TaskFiles(
                [$"types {keptTypes}/{truthTypes.Count}", $"methods {keptMethods}/{truthMethods.Count}"],
                truthMethods.Where(m => !skeletonOutput.Contains(m + "(", StringComparison.Ordinal)).Take(10).ToList(),
                []));
    }

    private static void CollectPublicApi(string text, HashSet<string> types, HashSet<string> methods)
    {
        SyntaxNode root;
        try
        {
            root = CSharpSyntaxTree.ParseText(text).GetRoot();
        }
        catch (Exception)
        {
            return;
        }

        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (!IsPublicApi(type.Modifiers))
                continue;
            types.Add(type.Identifier.ValueText);
            foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
                if (IsPublicApi(method.Modifiers))
                    methods.Add(method.Identifier.ValueText);
        }
    }

    private static bool IsPublicApi(SyntaxTokenList modifiers)
        => modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword));

    private static IReadOnlyList<string> EnumerateCsFiles(string dir)
    {
        var full = Path.GetFullPath(dir);
        if (!Directory.Exists(full))
            return [];
        return Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsExcluded(p))
            .Select(p => Path.GetRelativePath(full, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsExcluded(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
               || p.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
               || p.Contains("/.git/", StringComparison.Ordinal)
               || p.Contains("/.fuse/", StringComparison.Ordinal)
               || p.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
               || p.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
               || p.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<(string Name, string Dir)> ResolveTargets(EvalOptions options)
    {
        var corpusRoot = options.ResolvedCorpusRoot;
        var targets = new List<(string, string)>();
        if (Directory.Exists(corpusRoot))
        {
            foreach (var repo in Directory.GetDirectories(corpusRoot).OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(repo);
                if (options.RepoFilter is not null && !name.Equals(options.RepoFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                targets.Add((name, repo));
            }
        }

        if (targets.Count > 0)
            return targets;

        // Offline fallback: the in-repo fixtures, so the suite always produces a number without the corpus.
        var repoRoot = Path.GetFullPath(Path.Combine(options.BenchRoot, "..", ".."));
        foreach (var fixture in new[] { "SampleShop", "OrderingApp" })
        {
            var dir = Path.Combine(repoRoot, "tests", "fixtures", fixture);
            if (Directory.Exists(dir))
                targets.Add((fixture, dir));
        }

        return targets;
    }
}
