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
    string? Note)
{
    /// <summary>How many cause lines fuse printed, one under an error in a file the case did not edit.</summary>
    public int CauseLines { get; init; }

    /// <summary>How many bytes fuse printed, so the cause lines' share of a response is visible.</summary>
    public int OutputBytes { get; init; }

    /// <summary>How many bytes the cause lines account for.</summary>
    public int CauseBytes { get; init; }

    /// <summary>How many files the check checked, or null when it did not answer.</summary>
    public int? FilesChecked { get; init; }

    /// <summary>How many documents binding the targets bound, one per target framework; 0 for a kept answer.</summary>
    public int? DocumentsBound { get; init; }

    /// <summary>Whether the check checked whole projects.</summary>
    public bool WholeProjectsBound { get; init; }

    /// <summary>Whether the check answered from the answer it kept, binding nothing.</summary>
    public bool KeptAnswerHit { get; init; }

    /// <summary>For how many files the check reused the errors of an earlier check, or null when it did not answer.</summary>
    public int? FilesWithReusedErrors { get; init; }

    /// <summary>How many errors the answer printed.</summary>
    public int PrintedErrors { get; init; }

    /// <summary>How many errors the answer counts, printed or not.</summary>
    public int TotalErrors { get; init; }

    /// <summary>How many errors the results file holds, for a capped answer; null when the answer printed every error.</summary>
    public int? ResultsFileErrors { get; init; }
}

/// <summary>
///     Applies mutations that change a declaration other files use (single edits and 2-3 edit sequences across projects), then compares the
///     errors <c>fuse check</c> reports with the errors a real <c>dotnet build</c> reports beyond the HEAD build.
/// </summary>
internal static partial class CorrectnessSuite
{
    /// <summary>
    ///     Measures <c>fuse check</c> on every case of <paramref name="truth"/>, which holds the plan and the real build of
    ///     HEAD and of every case, and compares the errors it reports with the errors each build reported beyond HEAD's.
    /// </summary>
    public static async Task<object> RunAsync(EvalRepo repo, SolutionInfo solution, TruthFile truth)
    {
        if (!await repo.IsCleanAsync())
            throw new InvalidOperationException($"{repo.Root} has uncommitted changes; the suite needs a clean tree");
        if (!truth.IsComplete)
            throw new InvalidOperationException($"the truth for {truth.Key} is not complete");
        var head = truth.Head!;
        Console.WriteLine($"[correctness] {repo.Name}: HEAD build...");
        var headBuild = await repo.BuildAsync();
        Console.WriteLine($"[correctness] HEAD build exit {headBuild.ExitCode}, {head.Names.Count} error(s) in the truth ({head.Origin})");
        var warm = await repo.FuseAsync("check");
        Console.WriteLine($"[correctness] fuse warm-up: {warm.Result.Output.Trim().Split('\n').Last()} ({warm.Milliseconds:0} ms)");

        // Every phase line already in the log belongs to an earlier call; draining them keeps each case to its own.
        var taken = new HashSet<string>(StringComparer.Ordinal);
        while (RequestPhases.TakeNewest(repo.EngineLogLines(), taken) is not null)
        {
        }

        var cases = new List<CorrectnessCase>();
        foreach (var planned in truth.Plan!)
        {
            var edits = planned.Edits;
            var item = truth.Cases[planned.Index];
            await CasePlanner.ApplyAsync(repo, planned);
            var logStart = repo.EngineLogLines().Count;
            var fuse = await repo.FuseAsync(["check", .. edits.Select(e => Path.Combine(repo.Root, e.Path))]);
            var logLines = repo.EngineLogLines().Skip(logStart).ToList();
            var phases = RequestPhases.TakeNewest(repo.EngineLogLines(), taken);
            var counts = CheckCounts.Parse(fuse.Result.Output, logLines, phases);

            // The cause lines `fuse check` prints under an error in a file this case did not edit.
            List<Match> Context() => [.. fuse.Result.Output.Split('\n').Select(l => CauseLine().Match(l.TrimEnd('\r'))).Where(m => m.Success)];
            var truthErrors = Subtract(item.Names, head.Names);
            var fuseErrors = EvalRepo.ParseFuseErrors(fuse.Result.Output);
            var fuseCount = EvalRepo.ParseFuseCount(fuse.Result.Output);
            var printedErrors = fuseErrors.Count;
            // A capped answer prints some errors and writes every one to its results file, so a file that holds them all
            // is what the case is classified on; matching only the printed ones would count the rest as missed.
            if (ResultsFileCounts.CappedResults(fuse.Result.Output) is { } cappedAnswer && ResultsFileCounts.ReadText(cappedAnswer.Path) is { } text
                && ResultsFileCounts.EngineUnsentIn(text) == 0 && ResultsFileCounts.CheckErrorsIn(text) == cappedAnswer.Total)
            {
                fuseErrors = EvalRepo.ParseFuseErrors(text);
            }

            var result = Classify(solution, planned.Index, edits, truthErrors, fuseErrors, fuseCount, fuse.Result.ExitCode, fuse.Milliseconds, item.Seconds,
                fuse.Result.ExitCode is 0 or 1 ? null : fuse.Result.Output.Trim()) with
            {
                CauseLines = Context().Count(m => m.Success),
                OutputBytes = System.Text.Encoding.UTF8.GetByteCount(fuse.Result.Output),
                CauseBytes = System.Text.Encoding.UTF8.GetByteCount(string.Join("\n", Context().Select(m => m.Value))),
                FilesChecked = counts.FilesChecked,
                DocumentsBound = counts.DocumentsBound,
                WholeProjectsBound = counts.WholeProjectsBound,
                KeptAnswerHit = counts.KeptAnswerHit,
                FilesWithReusedErrors = counts.FilesWithReusedErrors,
                PrintedErrors = printedErrors,
                TotalErrors = fuseCount,
            };
            // A capped answer proves its completeness with its results file: the file holds every error the summary
            // counts. A missing file or a short count is a defect verdict, not a silent null. An answered check the
            // engine logged nothing for is unmeasured, so a logging regression cannot silently zero the counts.
            if (ResultsFileCounts.CappedResults(fuse.Result.Output) is { } capped)
            {
                var resultsText = ResultsFileCounts.ReadText(capped.Path);
                var fileErrors = resultsText is null ? (int?)null : ResultsFileCounts.CheckErrorsIn(resultsText) + ResultsFileCounts.EngineUnsentIn(resultsText);
                result = result with { ResultsFileErrors = fileErrors };
                if (fileErrors != capped.Total)
                    result = result with { Verdict = "results-incomplete" };
            }

            if (result.Verdict != "results-incomplete" && fuse.Result.ExitCode is 0 or 1 && (!counts.HasCheckLine || phases is null))
                result = result with { Verdict = "unmeasured" };
            cases.Add(result);
            Console.WriteLine($"[correctness] {planned.Index + 1}/{truth.Count} {result.Verdict,-12} truth={truthErrors.Count,3} fuse={fuseCount,3} {fuse.Milliseconds,6:0} ms  {string.Join(" + ", edits.Select(e => $"{e.Kind} {e.Path}"))}");

            // The classification reads the edited files, so the tree is reset after it. The pause stands for the time an
            // agent takes between one edit's check and the next edit, in which the engine takes in the reset.
            await repo.ResetAsync();
            await Task.Delay(2000);
        }

        await repo.ResetAsync();
        var clean = await repo.IsCleanAsync();
        var breaking = cases.Count(c => c.Truth.Count > 0);
        var summary = new
        {
            suite = "correctness",
            repo = repo.Name,
            commit = await repo.HeadAsync(),
            fuseBuild = await repo.VersionAsync(),
            seed = truth.Seed,
            requested = truth.Count,
            cases = cases.Count,
            breaking,
            neutral = cases.Count - breaking,
            falseGreen = cases.Count(c => c.Verdict == "false-green"),
            falseRedCases = cases.Count(c => c.FalseRed.Count > 0),
            falseRedErrors = cases.Sum(c => c.FalseRed.Count),
            unverifiableErrors = cases.Sum(c => c.Unverifiable.Count),
            deferredByCompilerErrors = cases.Sum(c => c.DeferredByCompiler.Count),
            messageMismatchErrors = cases.Sum(c => c.MessageMismatch.Count),
            causeLines = cases.Sum(c => c.CauseLines),
            causeBytes = cases.Sum(c => c.CauseBytes),
            outputBytes = cases.Sum(c => c.OutputBytes),
            filesChecked = cases.Sum(c => c.FilesChecked ?? 0),
            documentsBound = cases.Sum(c => c.DocumentsBound ?? 0),
            filesWithReusedErrors = cases.Sum(c => c.FilesWithReusedErrors ?? 0),
            keptAnswerHits = cases.Count(c => c.KeptAnswerHit),
            partialMisses = cases.Count(c => c.Verdict == "partial"),
            exactAgreement = cases.Count(c => c.Verdict is "agree" or "agree-clean"),
            fileAgreement = cases.Count(c => c.FileAgreement),
            fuseUnanswered = cases.Count(c => c.FuseExit is not (0 or 1)),
            unmeasuredCases = cases.Count(c => c.Verdict == "unmeasured"),
            resultsIncompleteCases = cases.Count(c => c.Verdict == "results-incomplete"),
            cappedCasesComplete = cases.Where(c => c.TotalErrors > c.PrintedErrors).All(c => c.Verdict != "results-incomplete"),
            fuseMedianMs = Median(cases.Select(c => c.FuseMilliseconds)),
            buildMedianSeconds = Median(cases.Select(c => c.BuildSeconds)),
            fuseStats = CaseStats.Of(cases.Select(c => c.FuseMilliseconds / 1000)),
            dotnetStats = CaseStats.Of(cases.Select(c => c.BuildSeconds)),
            headBuildErrors = head.Names.Count,
            truthKey = truth.Key,
            truthOrigins = truth.Origins(),
            treeCleanAfter = clean,
            details = cases,
        };
        Console.WriteLine($"[correctness] {repo.Name}: {cases.Count} cases, {breaking} breaking, false green {summary.falseGreen}, false-red cases {summary.falseRedCases}, unverifiable errors {summary.unverifiableErrors}, deferred by csc {summary.deferredByCompilerErrors}, message mismatches {summary.messageMismatchErrors}, partial {summary.partialMisses}, exact {summary.exactAgreement}, file agreement {summary.fileAgreement}, tree clean {clean}");
        return summary;
    }

    /// <summary>
    ///     Compares Fuse's errors with the build's. Errors are matched by position (file, line, column); a match whose id
    ///     or message differs is counted as a message mismatch, not a disagreement (the build compiles against reference
    ///     assemblies, which omit private members, so it says "no definition" where Fuse says "inaccessible"). A Fuse
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
        // `fuse check` prints at most 20 errors; misses beyond what it printed are only real when its total is lower.
        var capped = fuseCount > fuse.Count;
        if (capped && fuseCount >= truth.Count)
            missed.Clear();
        var truthFiles = truth.Select(FileOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fuseFiles = fuse.Where(f => !unverifiable.Contains(f) && !deferred.Contains(f)).Select(FileOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fileAgreement = capped ? truthFiles.IsSupersetOf(fuseFiles) : truthFiles.SetEquals(fuseFiles);
        string verdict;
        // Exit 2 is Fuse not answering (Unanswered); anything else outside 0/1 means the process did not run (-1: failed to start).
        if (fuseExit is not (0 or 1))
            verdict = "fuse-unanswered";
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
    /// <remarks>
    ///     Analyzers and XML documentation checks run after that stage too, so the same build reports no analyzer or
    ///     documentation error for the project either: with a duplicate member (CS0111) added to a Jellyfin file that also
    ///     misindents a member, the build reports CS0111 alone, where without the duplicate it reports SA1137 and the rest.
    /// </remarks>
    private static bool CompilerSkippedBodies(SolutionInfo solution, string project, List<string> truth, string fuseError)
    {
        // Only the compiler's own errors say where csc stopped: a source generator reports its diagnostics (MVVMTK0022 in
        // the Community Toolkit) before declarations are compiled, so they appear next to declaration errors.
        var compilerErrors = truth.Where(t => string.Equals(solution.ProjectFileOf(FileOf(t)), project, StringComparison.OrdinalIgnoreCase) && IsCompiler(t)).ToList();
        // The compiler's XML documentation checks are skipped after any compiler error, a body error included: in a
        // Jellyfin probe a CS0103 made the build drop CS1591 while its analyzers still reported.
        if (compilerErrors.Count > 0 && IsDocumentation(fuseError))
            return true;
        return compilerErrors.Count > 0
               && (InBody(solution.Root, fuseError) || AfterDeclarations(fuseError))
               && compilerErrors.All(e => !InBody(solution.Root, e) && !AfterDeclarations(e));
    }

    private static bool IsCompiler(string error) => Canonical().Match(error).Groups["id"].Value.StartsWith("CS", StringComparison.Ordinal);

    /// <summary>
    ///     True for a diagnostic csc only reports once declarations compile: any analyzer id (not <c>CS</c>), and the
    ///     compiler's XML documentation checks, CS1570 to CS1592.
    /// </summary>
    private static bool AfterDeclarations(string error)
    {
        var id = Canonical().Match(error).Groups["id"].Value;
        if (id.Length == 0)
            return false;
        return !id.StartsWith("CS", StringComparison.Ordinal) || IsDocumentation(error) || FlowAnalysisIds.Contains(id);
    }

    /// <summary>
    ///     Flow analysis errors, which the compiler finds while compiling method bodies but places on a declaration (the
    ///     method's or the constructor's name), so <see cref="InBody(string, string)"/> does not see them as body errors. In a Jellyfin case
    ///     a removed using left Task unresolved in a signature; the build stopped at the declaration errors and never
    ///     reported the CS0161 the semantic model does.
    /// </summary>
    private static readonly HashSet<string> FlowAnalysisIds = new(StringComparer.Ordinal) { "CS0161", "CS0171", "CS0177", "CS0843" };

    /// <summary>True for the compiler's XML documentation checks, CS1570 to CS1592.</summary>
    private static bool IsDocumentation(string error)
    {
        var id = Canonical().Match(error).Groups["id"].Value;
        return id.StartsWith("CS", StringComparison.Ordinal)
               && int.TryParse(id[2..], System.Globalization.CultureInfo.InvariantCulture, out var number) && number is >= 1570 and <= 1592;
    }

    private static bool InBody(string root, string error)
    {
        var match = Canonical().Match(error);
        if (!match.Success || !match.Groups["line"].Success)
            return false;
        var path = Path.Combine(root, match.Groups["file"].Value);
        if (!File.Exists(path))
            return false;
        var source = File.ReadAllText(path);
        var line = int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture) - 1;
        var col = int.Parse(match.Groups["col"].Value, System.Globalization.CultureInfo.InvariantCulture) - 1;
        // An error inside an #if region was reported by the target framework that defines its symbol, and parsed without
        // it the region is disabled text. So the file is parsed as it is and with every symbol its #if lines name.
        var symbols = Conditional().Matches(source).SelectMany(m => Identifier().Matches(m.Groups["condition"].Value).Select(i => i.Value))
            .Where(s => s is not ("true" or "false")).Distinct(StringComparer.Ordinal).ToArray();
        return InBody(CSharpSyntaxTree.ParseText(source), line, col)
               || (symbols.Length > 0 && InBody(CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithPreprocessorSymbols(symbols)), line, col));
    }

    private static bool InBody(SyntaxTree tree, int line, int col)
    {
        var text = tree.GetText();
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

    [GeneratedRegex(@"^[ \t]*#[ \t]*(?:if|elif)\b(?<condition>.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Conditional();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

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

    // The cause line `fuse check` prints under an error in a file the case did not edit, indented by two spaces.
    [GeneratedRegex(@"^ {2}(?<state>changed|removed): (?<declaration>.+)$")]
    private static partial Regex CauseLine();
}
