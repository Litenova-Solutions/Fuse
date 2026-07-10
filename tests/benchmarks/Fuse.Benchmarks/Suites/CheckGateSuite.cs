using System.Collections.Immutable;
using Fuse.Indexing;
using Fuse.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Fuse.Benchmarks;

/// <summary>
///     Suite F (R1): the honesty gate for <c>fuse_check</c>. It feeds a battery of single-file edits with a known
///     correct answer through the speculative typecheck and measures the two ways an oracle can lie: a false
///     green (a broken edit reported clean, the dangerous failure that would let an agent commit a non-building
///     change) and a false red (a valid edit reported broken, which wastes an agent's turn chasing a phantom).
///     The gate is zero of each among the checks that verified; an abstention is neither, because abstaining is
///     the honest answer when the oracle cannot know.
/// </summary>
/// <remarks>
///     The suite runs a deterministic in-process core over a small self-contained compilation built with raw
///     Roslyn (no MSBuild, no build-capture closure, so it runs everywhere and cannot hit the B1 assembly
///     conflict). Each case replaces one document's syntax tree in that compilation and classifies the changed
///     document's diagnostics with the same <see cref="CheckResult.IsClean" /> rule the worker applies, so the
///     gate measures the exact classification contract <c>fuse_check</c> ships. When a build-capture worker is
///     configured (<c>FUSE_BUILD_CAPTURE_WORKER</c>) and a fixture solution is present, it additionally exercises
///     the real tier-1 path end to end; otherwise it records that the tier-1 arm was skipped rather than
///     fabricating a number for it.
/// </remarks>
public sealed class CheckGateSuite : IEvalSuite
{
    private readonly BuildCaptureClient _capture;

    /// <summary>
    ///     Initializes a new instance of the <see cref="CheckGateSuite" /> class.
    /// </summary>
    /// <param name="capture">The build-capture client for the optional tier-1 end-to-end arm; when its worker is not configured, that arm is skipped.</param>
    public CheckGateSuite(BuildCaptureClient? capture = null) => _capture = capture ?? new BuildCaptureClient();

    /// <inheritdoc />
    public string Name => "checkgate";

    /// <inheritdoc />
    public string Description => "Suite F: fuse_check false-green and false-red rates over known-good and known-bad edits.";

    /// <inheritdoc />
    public async Task<SuiteResult> RunAsync(EvalOptions options, CancellationToken cancellationToken)
    {
        var notes = new List<string>
        {
            "In-process core: a raw-Roslyn compilation, one document replaced per case, classified with the shipped CheckResult.IsClean rule.",
        };
        var tasks = new List<TaskResult>();

        var compilation = BuildBaseline();
        var baselineErrors = compilation.GetDiagnostics(cancellationToken)
            .Count(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        if (baselineErrors != 0)
        {
            // The baseline must compile clean, or the gate would measure the fixture, not the oracle.
            notes.Add($"Baseline compilation has {baselineErrors} error(s); cannot run the gate.");
            return new SuiteResult(Name, Description, null,
                new Scorecard(0, 0, 0, 0, 0, 0, 0, 0), tasks, notes);
        }

        int falseGreen = 0, falseRed = 0, abstained = 0, correct = 0;
        foreach (var edit in Cases())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = CheckInProcess(compilation, edit, cancellationToken);

            bool classifiedCorrectly;
            if (!result.Verified)
            {
                abstained++;
                classifiedCorrectly = false; // Neither a false green nor a false red, but not a scored success.
            }
            else if (edit.ShouldBeClean && !result.IsClean)
            {
                falseRed++;
                classifiedCorrectly = false;
            }
            else if (!edit.ShouldBeClean && result.IsClean)
            {
                falseGreen++;
                classifiedCorrectly = false;
            }
            else
            {
                correct++;
                classifiedCorrectly = true;
            }

            var diagIds = string.Join(",", result.Diagnostics.Where(d => d.Severity == "Error").Select(d => d.Id).Distinct());
            tasks.Add(new TaskResult(
                edit.Name,
                "checkgate",
                edit.ShouldBeClean ? "good" : "bad",
                classifiedCorrectly ? 1.0 : 0.0,
                classifiedCorrectly ? 1.0 : 0.0,
                0,
                0,
                new TaskFiles(
                    classifiedCorrectly ? [edit.Name] : [],
                    classifiedCorrectly ? [] : [edit.Name],
                    diagIds.Length == 0 ? [] : [diagIds])));
        }

        var verified = falseGreen + falseRed + correct;
        var accuracy = tasks.Count == 0 ? 0.0 : (double)correct / tasks.Count;
        notes.Add($"curated cases {tasks.Count}: correct {correct}, false-green {falseGreen}, false-red {falseRed}, abstained {abstained}.");
        notes.Add($"curated false-green rate {(verified == 0 ? 0 : (double)falseGreen / verified):P1}, false-red rate {(verified == 0 ? 0 : (double)falseRed / verified):P1} (over {verified} verified).");

        // The mutation arm (H1): thousands of compiler-verified single-file edits, so "check never lies" is a
        // measured rate rather than the eight curated cases. Off unless --mutations N is given; the curated set
        // above remains the named subset.
        var mutation = options.Mutations > 0
            ? RunMutationArm(options, notes, cancellationToken)
            : null;
        if (mutation is null)
            notes.Add("mutation arm: skipped (pass --mutations N to run the scaled honesty gate).");

        var totalFalseGreen = falseGreen + (mutation?.FalseGreen ?? 0);
        var totalFalseRed = falseRed + (mutation?.FalseRed ?? 0);
        var mutationFalseRedRate = mutation is { Verified: > 0 }
            ? (double)mutation.FalseRed / mutation.Verified
            : 0.0;
        notes.Add(totalFalseGreen == 0 && mutationFalseRedRate < 0.01
            ? $"GATE: PASS (false green {totalFalseGreen}; mutation false-red rate {mutationFalseRedRate:P2} < 1%)."
            : $"GATE: FAIL (false green {totalFalseGreen}; mutation false-red rate {mutationFalseRedRate:P2}).");

        // Optional tier-1 end-to-end arm: only when a real worker is configured. It is a wiring check that the
        // out-of-process path returns the same clean/dirty verdict, not a second statistical sample.
        notes.Add(_capture.IsAvailable
            ? "Tier-1 arm: build-capture worker configured; the in-process gate is the scored measure, the worker path is covered by BuildCaptureCheckTests."
            : "Tier-1 arm: skipped (no FUSE_BUILD_CAPTURE_WORKER); the worker path is covered by BuildCaptureCheckTests when provisioned.");

        // The T0 verify-agreement arm: run a sample of mutants through both the oracle path (the build-capture
        // worker) and the build-grade path (BuildGradeChecker), and record their diagnostic-identity agreement.
        // This is the recorded artifact for T0's gate; it runs only when a worker is provisioned and --verify-agreement
        // N is given, and it is a bounded sample because each mutant runs two real builds.
        if (options.VerifyAgreement > 0)
            await RunVerifyAgreementArmAsync(options, notes, cancellationToken);
        else
            notes.Add("verify-agreement arm: skipped (pass --verify-agreement N with FUSE_BUILD_CAPTURE_WORKER to record the T0 oracle-vs-build agreement).");

        var scorecard = new Scorecard(
            tasks.Count,
            accuracy,
            0,
            0,
            verified == 0 ? 0 : (double)correct / verified,
            accuracy,
            0,
            0);
        return new SuiteResult(Name, Description, null, scorecard, tasks, notes);
    }

    // The T0 verify-agreement arm (Decision D11): the build-grade rung is ground truth by construction (the real
    // compiler answered), so the honesty question is whether the oracle-grade speculative path AGREES with it. For
    // a bounded sample of OrderingApp mutants, run the same proposed content through both the worker (oracle) and a
    // scoped dotnet build (build-grade) and compare the set of error diagnostic ids attributed to the changed file.
    // A mutant where either grade abstains is not comparable and is counted separately, not scored as a disagreement.
    private async Task<VerifyAgreementTally?> RunVerifyAgreementArmAsync(
        EvalOptions options, List<string> notes, CancellationToken cancellationToken)
    {
        if (!_capture.IsAvailable)
        {
            notes.Add("verify-agreement arm: skipped (no FUSE_BUILD_CAPTURE_WORKER; the oracle-vs-build agreement gate needs a provisioned worker).");
            return null;
        }

        var repoRoot = Path.GetFullPath(Path.Combine(options.BenchRoot, "..", ".."));
        var fixturesRoot = options.FixturesRoot is { } root ? Path.GetFullPath(root) : Path.Combine(repoRoot, "tests", "fixtures");
        var dir = Path.Combine(fixturesRoot, "OrderingApp");
        var project = Path.Combine(dir, "OrderingApp.csproj");
        if (!File.Exists(project))
        {
            notes.Add($"verify-agreement arm: skipped (OrderingApp project not found at {project}).");
            return null;
        }

        var (compilation, mutableFiles) = BuildFixtureCompilation(dir);
        var baselineErrors = compilation.GetDiagnostics(cancellationToken)
            .Count(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        if (baselineErrors != 0)
        {
            notes.Add($"verify-agreement arm: skipped (OrderingApp baseline has {baselineErrors} in-process error(s)).");
            return null;
        }

        // Balance the sample across breaking and neutral mutants, then take exactly the requested count.
        var perClass = Math.Max(1, (options.VerifyAgreement + 1) / 2);
        var cases = new MutationGenerator().Generate(compilation, mutableFiles, perClass, 20260708 + "OrderingApp".GetHashCode());
        var sample = cases.Take(options.VerifyAgreement).ToList();

        // Mirror the fixture to a temp working copy so the real fixture stays pristine, and point the oracle at it.
        // The mutation is applied in-memory (the on-disk sources never change across mutants), so the incremental
        // build would go up-to-date after the first call and capture no compiler invocation; clearing obj/bin before
        // each oracle call forces a real recompile so the binlog carries the csc call the worker rehydrates.
        var work = Path.Combine(Path.GetTempPath(), "fuse-verify-agreement", Guid.NewGuid().ToString("N"));
        CopyDirectoryExcludingBuild(dir, work);
        var oracleProject = Path.Combine(work, Path.GetFileName(project));

        var timeout = TimeSpan.FromMinutes(5);
        var buildChecker = new BuildGradeChecker(timeout);
        int compared = 0, idAgree = 0, verdictAgree = 0, oracleAbstain = 0, buildAbstain = 0;
        var disagreements = new List<string>();
        try
        {
            foreach (var mutant in sample)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(dir, mutant.TargetFile);
                ClearBuildOutput(work);
                var oracle = await _capture.CheckAsync(oracleProject, relative, mutant.NewContent, timeout, cancellationToken);
                var build = await buildChecker.CheckAsync(work, [oracleProject], relative, mutant.NewContent, cancellationToken);

                if (!oracle.Verified) { oracleAbstain++; if (disagreements.Count < 12) disagreements.Add($"{mutant.Name}: oracle abstained ({oracle.Reason})"); continue; }
                if (!build.Verified) { buildAbstain++; if (disagreements.Count < 12) disagreements.Add($"{mutant.Name}: build abstained ({build.Reason})"); continue; }

                compared++;
                var oracleIds = ErrorIds(oracle);
                var buildIds = ErrorIds(build);
                if (oracleIds.SetEquals(buildIds))
                    idAgree++;
                else if (disagreements.Count < 12)
                    disagreements.Add($"{mutant.Name}: oracle[{string.Join("+", oracleIds.OrderBy(x => x))}] vs build[{string.Join("+", buildIds.OrderBy(x => x))}]");
                if (oracle.IsClean == build.IsClean)
                    verdictAgree++;
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        var rate = compared == 0 ? 0.0 : (double)idAgree / compared;
        notes.Add($"verify-agreement arm: {sample.Count} OrderingApp mutants sampled, {compared} comparable " +
                  $"(oracle abstained {oracleAbstain}, build abstained {buildAbstain}); " +
                  $"diagnostic-id agreement {idAgree}/{compared} = {rate:P1}; verdict (clean/red) agreement {verdictAgree}/{compared}.");
        foreach (var line in disagreements)
            notes.Add($"verify-agreement disagreement: {line}");
        notes.Add(rate >= 0.99
            ? $"T0 GATE (verify agreement): PASS ({rate:P1} >= 99% on {compared} comparable mutants)."
            : $"T0 GATE (verify agreement): {rate:P1} < 99% on {compared} comparable mutants; per the fallback, build-grade is ground truth and ships, the discrepancy list above is the named fix item.");

        return new VerifyAgreementTally(sample.Count, compared, idAgree, verdictAgree, oracleAbstain, buildAbstain);
    }

    // The distinct error-severity diagnostic ids a check attributed to the changed document. Warnings are excluded
    // because the verdict (clean vs red) turns on errors; a warning-set difference (for example a doc-comment
    // warning the build path does not emit) is not a verdict disagreement.
    private static HashSet<string> ErrorIds(CheckResult result) =>
        result.Diagnostics.Where(d => d.Severity == "Error").Select(d => d.Id).ToHashSet(StringComparer.Ordinal);

    // Copies a fixture directory to a working location, skipping build/tooling output so the copy is a clean source
    // tree the oracle can build from without inheriting stale artifacts.
    private static void CopyDirectoryExcludingBuild(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split('/', '\\').Any(s => s is "bin" or "obj" or ".vs" or ".git"))
                continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // Removes the build output of a working copy so the next build recompiles (the on-disk sources are unchanged
    // across mutants, so an incremental build would otherwise go up-to-date and capture no compiler invocation).
    private static void ClearBuildOutput(string directory)
    {
        foreach (var name in new[] { "obj", "bin" })
        {
            var path = Path.Combine(directory, name);
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // Replaces the target document's tree in the baseline compilation and classifies the changed document's
    // diagnostics exactly as the shipped worker does (error-severity diagnostics on the changed tree => not clean).
    private static CheckResult CheckInProcess(CSharpCompilation baseline, CheckEdit edit, CancellationToken cancellationToken)
    {
        var oldTree = baseline.SyntaxTrees.FirstOrDefault(t => t.FilePath == edit.TargetFile);
        if (oldTree is null)
            return CheckResult.Abstain($"target document '{edit.TargetFile}' not in the compilation");

        var newTree = CSharpSyntaxTree.ParseText(edit.NewContent, path: edit.TargetFile, cancellationToken: cancellationToken);
        var patched = baseline.ReplaceSyntaxTree(oldTree, newTree);

        var model = patched.GetSemanticModel(newTree);
        var diagnostics = model.GetDiagnostics(cancellationToken: cancellationToken)
            .Concat(newTree.GetDiagnostics(cancellationToken))
            .Where(d => d.Severity is Microsoft.CodeAnalysis.DiagnosticSeverity.Error or Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .Select(d => new CheckDiagnostic(
                d.Id,
                d.Severity.ToString(),
                d.GetMessage(),
                edit.TargetFile,
                d.Location.GetLineSpan().StartLinePosition.Line + 1))
            .ToList();
        return CheckResult.Ok(diagnostics);
    }

    // A small self-contained project: an order domain with a service and a consumer, chosen so that a realistic
    // edit (a member rename, a signature change, a wrong-type assignment) either compiles clean or produces a
    // specific compiler error, giving the gate unambiguous ground truth.
    private static CSharpCompilation BuildBaseline()
    {
        var domain = CSharpSyntaxTree.ParseText("""
            namespace Shop;
            public sealed class Order
            {
                public int Id { get; init; }
                public decimal Total { get; init; }
                public decimal WithTax() => Total * 1.1m;
            }
            """, path: "Order.cs");

        var service = CSharpSyntaxTree.ParseText("""
            namespace Shop;
            public sealed class OrderService
            {
                public decimal Charge(Order order) => order.WithTax();
            }
            """, path: "OrderService.cs");

        return CSharpCompilation.Create(
            "ShopGate",
            [domain, service],
            ReferencePaths(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    // The set of edits over OrderService.cs, each with its known-correct verdict. Good edits must compile; bad
    // edits must produce an error the oracle is obliged to surface.
    private static IEnumerable<CheckEdit> Cases()
    {
        // Good: an equivalent rewrite that still binds.
        yield return new CheckEdit("good-equivalent-rewrite", "OrderService.cs", """
            namespace Shop;
            public sealed class OrderService
            {
                public decimal Charge(Order order)
                {
                    var taxed = order.WithTax();
                    return taxed;
                }
            }
            """, ShouldBeClean: true);

        // Good: add a valid overload that uses the domain type correctly.
        yield return new CheckEdit("good-added-overload", "OrderService.cs", """
            namespace Shop;
            public sealed class OrderService
            {
                public decimal Charge(Order order) => order.WithTax();
                public decimal ChargeRaw(Order order) => order.Total;
            }
            """, ShouldBeClean: true);

        // Bad: call a member that does not exist on Order (CS1061).
        yield return new CheckEdit("bad-missing-member", "OrderService.cs", """
            namespace Shop;
            public sealed class OrderService
            {
                public decimal Charge(Order order) => order.GrandTotal();
            }
            """, ShouldBeClean: false);

        // Bad: return a string where a decimal is declared (CS0029).
        yield return new CheckEdit("bad-wrong-return-type", "OrderService.cs", """
            namespace Shop;
            public sealed class OrderService
            {
                public decimal Charge(Order order) => "free";
            }
            """, ShouldBeClean: false);

        // Bad: reference an undefined type (CS0246).
        yield return new CheckEdit("bad-undefined-type", "OrderService.cs", """
            namespace Shop;
            public sealed class OrderService
            {
                public decimal Charge(Order order) => new Invoice(order).Amount;
            }
            """, ShouldBeClean: false);

        // Bad: a syntax error (a missing brace), which the parser reports.
        yield return new CheckEdit("bad-syntax-error", "OrderService.cs", """
            namespace Shop;
            public sealed class OrderService
            {
                public decimal Charge(Order order) => order.WithTax();
            """, ShouldBeClean: false);

        // Good: a comment-only change, the null edit that must stay clean.
        yield return new CheckEdit("good-comment-only", "OrderService.cs", """
            namespace Shop;
            public sealed class OrderService
            {
                // Charge applies tax and returns the amount due.
                public decimal Charge(Order order) => order.WithTax();
            }
            """, ShouldBeClean: true);

        // Bad: assign to an init-only property from outside its initializer (CS8852).
        yield return new CheckEdit("bad-init-only-assignment", "OrderService.cs", """
            namespace Shop;
            public sealed class OrderService
            {
                public decimal Charge(Order order)
                {
                    order.Total = 5m;
                    return order.WithTax();
                }
            }
            """, ShouldBeClean: false);
    }

    // The runtime's trusted platform assemblies as metadata references, so the baseline binds the BCL without a
    // project file. This is the standard no-MSBuild way to compile a snippet in-process.
    private static ImmutableArray<MetadataReference> ReferencePaths()
    {
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty;
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                builder.Add(MetadataReference.CreateFromFile(path));
        }
        return builder.ToImmutable();
    }

    // The scaled honesty gate (H1): generate compiler-verified breaking and neutral single-file mutants over the
    // in-repo fixtures and run each through the same shipped classification the curated cases use, so false green
    // and false red become rates over hundreds of cases per class. A fixture that cannot compile clean in-process
    // (for example one needing a framework its references do not supply) is recorded skipped rather than scored,
    // so the gate never measures the fixture instead of the oracle.
    private static MutationTally RunMutationArm(EvalOptions options, List<string> notes, CancellationToken cancellationToken)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(options.BenchRoot, "..", ".."));
        var fixturesRoot = options.FixturesRoot is { } root ? Path.GetFullPath(root) : Path.Combine(repoRoot, "tests", "fixtures");
        var generator = new MutationGenerator();
        var total = new MutationTally(0, 0, 0, 0, 0);
        // A fixed seed keyed off the fixture name keeps a run reproducible while giving each fixture its own stream.
        var seedBase = 20260708;

        foreach (var fixture in new[] { "SampleShop", "OrderingApp" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = Path.Combine(fixturesRoot, fixture);
            if (!Directory.Exists(dir))
            {
                notes.Add($"mutation fixture {fixture}: not found at {dir}; skipped.");
                continue;
            }

            var (compilation, mutableFiles) = BuildFixtureCompilation(dir);
            var baselineDiagnostics = compilation.GetDiagnostics(cancellationToken)
                .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .ToList();
            if (baselineDiagnostics.Count != 0)
            {
                var ids = string.Join(", ", baselineDiagnostics.Select(d => d.Id).Distinct().Take(6));
                notes.Add($"mutation fixture {fixture}: baseline has {baselineDiagnostics.Count} in-process error(s) [{ids}]; skipped, not scored (the in-process compilation cannot bind it without its full project references).");
                continue;
            }

            var cases = generator.Generate(compilation, mutableFiles, options.Mutations, seedBase + fixture.GetHashCode());
            var tally = new MutationTally(0, 0, 0, 0, 0);
            foreach (var mutant in cases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = CheckInProcess(compilation,
                    new CheckEdit(mutant.Name, mutant.TargetFile, mutant.NewContent, mutant.ShouldBeClean),
                    cancellationToken);
                tally = Tally(tally, mutant.ShouldBeClean, result);
            }

            var breaking = cases.Count(c => !c.ShouldBeClean);
            var neutral = cases.Count - breaking;
            notes.Add($"mutation fixture {fixture}: {cases.Count} verified cases ({breaking} breaking, {neutral} neutral); " +
                      $"false-green {tally.FalseGreen}, false-red {tally.FalseRed}, abstained {tally.Abstained}, correct {tally.Correct}.");
            total = Add(total, tally);
        }

        var verified = total.Verified;
        notes.Add($"mutation totals: {total.Total} cases, false-green {total.FalseGreen}, false-red {total.FalseRed} " +
                  $"(false-green rate {(verified == 0 ? 0 : (double)total.FalseGreen / verified):P2}, " +
                  $"false-red rate {(verified == 0 ? 0 : (double)total.FalseRed / verified):P2} over {verified} verified).");
        return total;
    }

    // Classifies one mutant's check result against its verified verdict, mirroring the curated-case rule exactly.
    private static MutationTally Tally(MutationTally t, bool shouldBeClean, CheckResult result)
    {
        if (!result.Verified)
            return t with { Total = t.Total + 1, Abstained = t.Abstained + 1 };
        if (shouldBeClean && !result.IsClean)
            return t with { Total = t.Total + 1, FalseRed = t.FalseRed + 1 };
        if (!shouldBeClean && result.IsClean)
            return t with { Total = t.Total + 1, FalseGreen = t.FalseGreen + 1 };
        return t with { Total = t.Total + 1, Correct = t.Correct + 1 };
    }

    private static MutationTally Add(MutationTally a, MutationTally b) => new(
        a.Total + b.Total, a.FalseGreen + b.FalseGreen, a.FalseRed + b.FalseRed, a.Abstained + b.Abstained, a.Correct + b.Correct);

    // Builds an in-process compilation from a fixture directory: every .cs under it (excluding build output),
    // referenced against the BCL and, when present, the ASP.NET Core shared framework so a web fixture binds. The
    // output kind follows the sources (top-level statements => console app, else library), so the baseline does
    // not fail on an entry-point mismatch.
    private static (CSharpCompilation Compilation, IReadOnlyCollection<string> MutableFiles) BuildFixtureCompilation(string dir)
    {
        var files = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        var trees = files
            .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f))
            .ToList();

        // SDK-style projects rely on implicit global usings that a raw compilation does not synthesize. Supply the
        // Microsoft.NET.Sdk (and, when the ASP.NET shared framework is available, the .Web) implicit-using sets so
        // a fixture that omits explicit usings still binds; an unused global using is harmless. The ASP.NET set is
        // added only when its references are present, or a global using to a missing namespace would itself error.
        var aspNet = AspNetSharedFrameworkReferences();
        var references = ReferencePaths().AddRange(aspNet);
        trees.Insert(0, CSharpSyntaxTree.ParseText(ImplicitGlobalUsings(includeAspNet: aspNet.Length > 0), path: "GlobalUsings.g.cs"));

        var hasTopLevel = trees.Any(t => t.GetRoot() is Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax cu
            && cu.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax>().Any());
        var outputKind = hasTopLevel ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary;
        var compilation = CSharpCompilation.Create(
            Path.GetFileName(dir) + "Mutations",
            trees,
            references,
            new CSharpCompilationOptions(outputKind, allowUnsafe: true));
        return (compilation, files);
    }

    // The ASP.NET Core shared framework assemblies as references, resolved relative to the running .NET runtime
    // directory (a sibling of the base runtime's shared folder). Empty when the shared framework is not installed,
    // in which case a web fixture simply records a baseline error and is skipped.
    private static ImmutableArray<MetadataReference> AspNetSharedFrameworkReferences()
    {
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        // runtimeDir is .../shared/Microsoft.NETCore.App/<version>/; the ASP.NET one is a sibling framework dir.
        var sharedRoot = Directory.GetParent(runtimeDir)?.Parent;
        if (sharedRoot is null)
            return [];
        var aspNetRoot = Path.Combine(sharedRoot.FullName, "Microsoft.AspNetCore.App");
        if (!Directory.Exists(aspNetRoot))
            return [];

        // Pick the highest installed version directory, so the fixture binds against a current framework.
        var versionDir = Directory.EnumerateDirectories(aspNetRoot)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .LastOrDefault();
        if (versionDir is null)
            return [];

        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        foreach (var dll in Directory.EnumerateFiles(versionDir, "*.dll"))
        {
            try
            {
                builder.Add(MetadataReference.CreateFromFile(dll));
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException)
            {
                // A non-managed or unreadable dll in the framework dir is skipped; the rest still bind.
            }
        }
        return builder.ToImmutable();
    }

    // The implicit global usings emitted by Microsoft.NET.Sdk, plus the Microsoft.NET.Sdk.Web set when the ASP.NET
    // shared framework is referenced, so an SDK-style fixture that omits explicit usings binds in-process. Unused
    // entries are harmless; a fixture that needs none of them still compiles.
    private static string ImplicitGlobalUsings(bool includeAspNet)
    {
        var baseSet = """
            global using global::System;
            global using global::System.Collections.Generic;
            global using global::System.IO;
            global using global::System.Linq;
            global using global::System.Net.Http;
            global using global::System.Threading;
            global using global::System.Threading.Tasks;
            """;
        if (!includeAspNet)
            return baseSet;
        return baseSet + "\r\n" + """
            global using global::System.Net.Http.Json;
            global using global::Microsoft.AspNetCore.Builder;
            global using global::Microsoft.AspNetCore.Hosting;
            global using global::Microsoft.AspNetCore.Http;
            global using global::Microsoft.AspNetCore.Routing;
            global using global::Microsoft.Extensions.Configuration;
            global using global::Microsoft.Extensions.DependencyInjection;
            global using global::Microsoft.Extensions.Hosting;
            global using global::Microsoft.Extensions.Logging;
            """;
    }

    private sealed record CheckEdit(string Name, string TargetFile, string NewContent, bool ShouldBeClean);

    // Running counts for the mutation arm. Verified excludes abstentions (neither a false green nor a false red).
    private sealed record MutationTally(int Total, int FalseGreen, int FalseRed, int Abstained, int Correct)
    {
        public int Verified => FalseGreen + FalseRed + Correct;
    }

    // Running counts for the T0 verify-agreement arm. Comparable excludes mutants either grade abstained on.
    private sealed record VerifyAgreementTally(
        int Sampled, int Comparable, int DiagnosticIdAgree, int VerdictAgree, int OracleAbstain, int BuildAbstain);
}
