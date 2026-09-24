using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Evals;

/// <summary>One mutation case: what changed, what the real build reported, and what fuse check reported.</summary>
internal sealed record CorrectnessCase(
    int Index,
    List<FileEdit> Edits,
    List<string> Truth,
    List<string> Fuse,
    int FuseCount,
    int FuseExit,
    double FuseMilliseconds,
    double BuildSeconds,
    string Verdict,
    List<string> FalseRed,
    List<string> Missed,
    List<string> Unverifiable,
    List<string> DeferredByCompiler,
    List<string> MessageMismatch,
    bool FileAgreement,
    string? Note);

/// <summary>
///     Suite 7.1: applies API-shape mutations (single edits and 2-3 edit sequences across projects), then compares the
///     errors <c>fuse check</c> reports with the errors a real <c>dotnet build</c> reports beyond the HEAD build.
/// </summary>
internal static partial class CorrectnessSuite
{
    public static async Task<object> RunAsync(EvalRepo repo, SolutionInfo solution, int count, int seed)
    {
        if (!await repo.IsCleanAsync())
            throw new InvalidOperationException($"{repo.Root} has uncommitted changes; the suite needs a clean tree");
        Console.WriteLine($"[correctness] {repo.Name}: HEAD build...");
        var head = await repo.BuildAsync();
        Console.WriteLine($"[correctness] HEAD build exit {head.ExitCode}, {head.Errors.Count} error(s), {head.Seconds:0.0} s");
        var warm = await repo.FuseAsync("check");
        Console.WriteLine($"[correctness] fuse warm-up: {warm.Result.Output.Trim().Split('\n').Last()} ({warm.Milliseconds:0} ms)");

        var sources = solution.CodeSources();
        var random = new Random(seed);
        var cases = new List<CorrectnessCase>();
        for (var i = 0; i < count; i++)
        {
            var editCount = random.NextDouble() < 0.7 ? 1 : random.Next(2, 4);
            var edits = PickEdits(repo, solution, sources, editCount, random);
            if (edits.Count == 0)
                continue;
            foreach (var edit in edits)
            {
                var full = Path.Combine(repo.Root, edit.Path);
                if (edit.NewText is null)
                    File.Delete(full);
                else
                    await File.WriteAllTextAsync(full, edit.NewText);
            }

            var fuse = await repo.FuseAsync(["check", .. edits.Select(e => Path.Combine(repo.Root, e.Path))]);
            var build = await repo.BuildAsync();
            var truth = Subtract(build.Errors, head.Errors);
            var fuseErrors = EvalRepo.ParseFuseErrors(fuse.Result.Output);
            var fuseCount = EvalRepo.ParseFuseCount(fuse.Result.Output);
            var result = Classify(solution, i, edits, truth, fuseErrors, fuseCount, fuse.Result.ExitCode, fuse.Milliseconds, build.Seconds,
                fuse.Result.ExitCode is 0 or 1 ? null : fuse.Result.Output.Trim());
            cases.Add(result);
            Console.WriteLine($"[correctness] {i + 1}/{count} {result.Verdict,-12} truth={truth.Count,3} fuse={fuseCount,3} {fuse.Milliseconds,6:0} ms  {string.Join(" + ", edits.Select(e => $"{e.Kind} {e.Path}"))}");
            await repo.ResetAsync();
        }

        await repo.ResetAsync();
        var clean = await repo.IsCleanAsync();
        var breaking = cases.Count(c => c.Truth.Count > 0);
        var summary = new
        {
            suite = "correctness",
            repo = repo.Name,
            seed,
            requested = count,
            cases = cases.Count,
            breaking,
            neutral = cases.Count - breaking,
            falseGreen = cases.Count(c => c.Verdict == "false-green"),
            falseRedCases = cases.Count(c => c.FalseRed.Count > 0),
            falseRedDiagnostics = cases.Sum(c => c.FalseRed.Count),
            unverifiableDiagnostics = cases.Sum(c => c.Unverifiable.Count),
            deferredByCompilerDiagnostics = cases.Sum(c => c.DeferredByCompiler.Count),
            messageMismatchDiagnostics = cases.Sum(c => c.MessageMismatch.Count),
            partialMisses = cases.Count(c => c.Verdict == "partial"),
            exactAgreement = cases.Count(c => c.Verdict is "agree" or "agree-clean"),
            fileAgreement = cases.Count(c => c.FileAgreement),
            fuseFailures = cases.Count(c => c.FuseExit is not (0 or 1)),
            fuseMedianMs = Median(cases.Select(c => c.FuseMilliseconds)),
            buildMedianSeconds = Median(cases.Select(c => c.BuildSeconds)),
            headBuildErrors = head.Errors.Count,
            treeCleanAfter = clean,
            details = cases,
        };
        Console.WriteLine($"[correctness] {repo.Name}: {cases.Count} cases, {breaking} breaking, false green {summary.falseGreen}, false-red cases {summary.falseRedCases}, unverifiable diagnostics {summary.unverifiableDiagnostics}, deferred by csc {summary.deferredByCompilerDiagnostics}, message mismatches {summary.messageMismatchDiagnostics}, partial {summary.partialMisses}, exact {summary.exactAgreement}, file agreement {summary.fileAgreement}, tree clean {clean}");
        return summary;
    }

    private static List<FileEdit> PickEdits(EvalRepo repo, SolutionInfo solution, List<string> sources, int editCount, Random random)
    {
        var edits = new List<FileEdit>();
        var usedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var attempt = 0; attempt < 200 && edits.Count < editCount; attempt++)
        {
            var file = sources[random.Next(sources.Count)];
            var project = solution.ProjectOf(file) ?? "";
            // Sequences spread over different projects when the solution has more than one.
            if (usedProjects.Contains(project) && solution.CodeProjects.Count > 1)
                continue;
            var kind = CompileMutator.Kinds[random.Next(CompileMutator.Kinds.Length)];
            var edit = CompileMutator.Mutate(file, Path.GetRelativePath(repo.Root, file).Replace('\\', '/'), kind, random);
            if (edit is null)
                continue;
            edits.Add(edit);
            usedProjects.Add(project);
        }

        return edits;
    }

    /// <summary>
    ///     Compares fuse's errors with the build's. Errors are matched by position (file, line, column); a match whose id
    ///     or message differs is counted as a message mismatch, not a disagreement (the build compiles against reference
    ///     assemblies, which omit private members, so it says "no definition" where fuse says "inaccessible"). A fuse
    ///     error in a project the build never compiled, because a project it references failed, is unverifiable: MSBuild
    ///     skips such projects, so the build has no answer there.
    /// </summary>
    private static CorrectnessCase Classify(SolutionInfo solution, int index, List<FileEdit> edits, List<string> truth, List<string> fuse, int fuseCount, int fuseExit, double ms, double buildSeconds, string? note)
    {
        var truthPositions = truth.Select(Position).ToList();
        var fusePositions = fuse.Select(Position).ToList();
        var truthKeys = truth.Select(Key).ToHashSet(StringComparer.Ordinal);
        var failedProjects = truth.Select(t => solution.ProjectFileOf(FileOf(t))).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unbuilt = solution.Projects.Where(p => SolutionInfo.ReferencesOf(p).Overlaps(failedProjects)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var falseRed = new List<string>();
        var unverifiable = new List<string>();
        var deferred = new List<string>();
        var mismatch = new List<string>();
        for (var i = 0; i < fuse.Count; i++)
        {
            if (truthPositions.Contains(fusePositions[i]))
            {
                if (!truthKeys.Contains(Key(fuse[i])))
                    mismatch.Add(fuse[i]);
                continue;
            }

            var project = solution.ProjectFileOf(FileOf(fuse[i]));
            if (project is not null && unbuilt.Contains(project))
                unverifiable.Add(fuse[i]);
            else if (project is not null && CompilerSkippedBodies(solution, project, truth, fuse[i]))
                deferred.Add(fuse[i]);
            else
                falseRed.Add(fuse[i]);
        }

        var missed = truth.Where((_, i) => !fusePositions.Contains(truthPositions[i])).ToList();
        // fuse prints at most 20 diagnostics; misses beyond what it printed are only real when its total is lower.
        var capped = fuseCount > fuse.Count;
        if (capped && fuseCount >= truth.Count)
            missed.Clear();
        var truthFiles = truth.Select(FileOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fuseFiles = fuse.Where(f => !unverifiable.Contains(f) && !deferred.Contains(f)).Select(FileOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fileAgreement = capped ? truthFiles.IsSupersetOf(fuseFiles) : truthFiles.SetEquals(fuseFiles);
        string verdict;
        // Exit 2 is fuse declining to answer; anything else outside 0/1 means the process did not run (-1: failed to start).
        if (fuseExit is not (0 or 1))
            verdict = "fuse-failed";
        else if (truth.Count > 0 && fuseCount == 0)
            verdict = "false-green";
        else if (falseRed.Count > 0)
            verdict = "false-red";
        else if (missed.Count > 0)
            verdict = "partial";
        else
            verdict = truth.Count == 0 ? "agree-clean" : "agree";
        return new CorrectnessCase(index, edits.Select(e => e with { NewText = null }).ToList(), truth, fuse, fuseCount, fuseExit, ms, buildSeconds, verdict, falseRed, missed, unverifiable, deferred, mismatch, fileAgreement, note);
    }

    /// <summary>
    ///     True when the build's errors in <paramref name="project"/> are all outside member bodies while the fuse error
    ///     is inside one. csc reports declaration errors and stops before binding method bodies, so it cannot report
    ///     the body error; fuse binds every body. The mutated files are still on disk when this runs.
    /// </summary>
    private static bool CompilerSkippedBodies(SolutionInfo solution, string project, List<string> truth, string fuseError)
    {
        var projectErrors = truth.Where(t => string.Equals(solution.ProjectFileOf(FileOf(t)), project, StringComparison.OrdinalIgnoreCase)).ToList();
        return projectErrors.Count > 0 && InBody(solution.Root, fuseError) && projectErrors.All(e => !InBody(solution.Root, e));
    }

    private static bool InBody(string root, string error)
    {
        var match = Canonical().Match(error);
        if (!match.Success || !match.Groups["line"].Success)
            return false;
        var path = Path.Combine(root, match.Groups["file"].Value);
        if (!File.Exists(path))
            return false;
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path));
        var text = tree.GetText();
        var line = int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture) - 1;
        var col = int.Parse(match.Groups["col"].Value, System.Globalization.CultureInfo.InvariantCulture) - 1;
        if (line >= text.Lines.Count)
            return false;
        var position = Math.Min(text.Lines[line].Start + col, text.Length - 1);
        // Documentation comments are compiled after declarations too, so csc skips their diagnostics the same way.
        if (tree.GetRoot().FindToken(position, findInsideTrivia: true).Parent?.AncestorsAndSelf().Any(n => n is DocumentationCommentTriviaSyntax) == true)
            return true;
        var node = tree.GetRoot().FindToken(position).Parent;
        return node?.AncestorsAndSelf().Any(n =>
            n is BlockSyntax { Parent: BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax }
            || n is ArrowExpressionClauseSyntax
            || n is ConstructorInitializerSyntax
            || n is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax } }
            || n is GlobalStatementSyntax) ?? false;
    }

    /// <summary>Multiset difference: the errors in <paramref name="after"/> beyond those in <paramref name="before"/>, matched on file, id and message.</summary>
    internal static List<string> Subtract(List<string> after, List<string> before)
    {
        var remaining = before.GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<string>();
        foreach (var error in after)
        {
            var key = Key(error);
            if (remaining.TryGetValue(key, out var n) && n > 0)
                remaining[key] = n - 1;
            else
                result.Add(error);
        }

        return result;
    }

    /// <summary>File, id and message of a canonical error line (the position is left out: both sides compile the same text, but a key without it tolerates column conventions).</summary>
    internal static string Key(string line)
    {
        var match = Canonical().Match(line);
        return match.Success ? $"{match.Groups["file"].Value}|{match.Groups["id"].Value}|{match.Groups["msg"].Value}" : line;
    }

    private static string Position(string line)
    {
        var match = Canonical().Match(line);
        return match.Success ? $"{match.Groups["file"].Value}|{match.Groups["line"].Value}|{match.Groups["col"].Value}" : line;
    }

    private static string FileOf(string line)
    {
        var match = Canonical().Match(line);
        return match.Success ? match.Groups["file"].Value : line;
    }

    internal static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    [GeneratedRegex(@"^(?<file>[^\r\n]+?)(?:\((?<line>\d+),(?<col>\d+)\))?: error (?<id>[A-Za-z]+\d+): (?<msg>.*)$")]
    private static partial Regex Canonical();
}
