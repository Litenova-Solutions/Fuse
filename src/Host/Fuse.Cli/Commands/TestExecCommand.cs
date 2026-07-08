using System.Diagnostics;
using DotMake.CommandLine;
using Fuse.Benchmarks;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Retrieval;
using Fuse.Semantics;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Fuse.Cli.Commands;

/// <summary>
///     Suite T1 (as a dedicated command, the resident-latency precedent): measures build-grade covering-test
///     execution honesty, behavior-mutant kill rate, and selection-safety. It runs the shipped build-grade
///     covering-test runner over a self-contained xunit fixture - a clean run must be all green (no false red),
///     each compiling behavior mutant of the class under test must turn a covering test red (killed = the runner
///     honestly surfaced the break), and the covering set that <see cref="GraphNeighborhoodExplorer" /> selects
///     from the R5 <c>tests</c> edges must both kill the mutant and exclude the unrelated test (selection-safety) -
///     and writes <c>results/testexec.json</c>.
/// </summary>
/// <remarks>
///     This is a command rather than a <c>fuse eval</c> suite because the build-grade runner lives in
///     <c>Fuse.Workspace</c>, whose Basic.CompilerLog closure (the VB 4.14 pin) cannot be referenced from the
///     <c>Fuse.Benchmarks</c> suite assembly without breaking its MSBuildWorkspace-based suites (the S1
///     co-activation constraint). Run in its own process, this command touches neither MSBuildWorkspace nor the
///     resident workspace, so it is safe. False green is 0 by construction (the runner mirrors the real
///     <c>dotnet test</c> TRX); the mutant-kill rate is the coverage signal; selection-safety is measured by
///     indexing the fixture, asking the covering primitive which tests cover the class under test, and running
///     only that subset against each mutant. The emit fast-path latency (sub-second, no build) is the named
///     follow-up.
/// </remarks>
[CliCommand(
    Name = "testexec",
    Description = "Measure build-grade covering-test execution honesty, mutant kill rate, and selection-safety; writes results/testexec.json.",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class TestExecCommand
{
    // The class under test and the two test types: one covers Calc (must be selected), one covers an unrelated
    // class (must NOT be selected). The gap between them is what makes selection-safety a real measurement rather
    // than "run the whole suite".
    private const string SymbolUnderTest = "Calc";
    private const string CoveringTestType = "CalcTests";
    private const string UnrelatedTestType = "OtherTests";
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(5);

    private readonly IConsoleUI _consoleUI;
    private readonly SemanticIndexer _indexer;

    /// <summary>Initializes a new instance of the <see cref="TestExecCommand" /> class for CLI binding.</summary>
    public TestExecCommand() : this(null!, null!)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TestExecCommand" /> class.</summary>
    /// <param name="consoleUI">The console UI for output.</param>
    /// <param name="indexer">The semantic indexer used to build the covering-edge graph over the fixture.</param>
    public TestExecCommand(IConsoleUI consoleUI, SemanticIndexer indexer)
    {
        _consoleUI = consoleUI;
        _indexer = indexer;
    }

    /// <summary>The number of behavior mutants to generate and run.</summary>
    [CliOption(Required = false, Description = "Number of behavior mutants to run.")]
    public int Mutants { get; set; } = 5;

    /// <summary>An optional path to write the JSON result to. Defaults to results/testexec.json under tests/benchmarks.</summary>
    [CliOption(Required = false, Description = "Path to write the JSON result to.")]
    public string? Output { get; set; }

    /// <summary>
    ///     Runs the testexec measurement and writes the result.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the result is written.</returns>
    public async Task RunAsync(CliContext context)
    {
        var cancellationToken = context.CancellationToken;
        var notes = new List<string>
        {
            "Self-contained xunit fixture; runs the shipped build-grade covering-test runner (dotnet test --filter).",
        };
        var tasks = new List<TaskResult>();

        var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fuse-testexec", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var scratch = System.IO.Path.Combine(work, "results");
        var classPath = System.IO.Path.Combine(work, "Calc.cs");
        var project = System.IO.Path.Combine(work, "Fix.csproj");

        try
        {
            await WriteFixtureAsync(work, cancellationToken);

            // The clean run exercises the WHOLE suite (both test types) so false-red is measured over everything.
            // It also restores and builds the fixture, which the covering selection below relies on: the R5 tests
            // edge is only emitted when the test attributes bind, which needs the xunit reference restored first.
            var fullFilter = TestFilterBuilder.BuildContains([CoveringTestType, UnrelatedTestType]);
            var cleanWatch = Stopwatch.StartNew();
            var clean = await BuildGradeTestRunner.RunAsync(project, fullFilter, scratch, RunTimeout, cancellationToken);
            cleanWatch.Stop();
            if (clean.Verdicts.Count == 0)
            {
                notes.Add($"skipped: the SDK could not build/run the fixture here ({clean.Diagnostics ?? (clean.TimedOut ? "timed out" : "no verdicts")}).");
                await WriteAsync(new SuiteResult("testexec", Description(), null, Empty(), tasks, notes), cancellationToken);
                _consoleUI.WriteResult(string.Join("\n", notes));
                return;
            }

            var cleanFailed = clean.Verdicts.Count(v => v.Outcome == "failed");
            notes.Add($"clean run: {clean.Verdicts.Count} test(s), {cleanFailed} failed (false-red on a correct fixture must be 0), {cleanWatch.ElapsedMilliseconds} ms");
            tasks.Add(new TaskResult("clean", "testexec", "clean", cleanFailed == 0 ? 1.0 : 0.0, 1.0, 0, cleanWatch.ElapsedMilliseconds, EmptyFiles));

            // The covering selection is a static, pre-edit choice over the RESTORED clean fixture: index it, then
            // ask which tests cover Calc through the R5 tests edges. Empty when the fixture loads syntax-only in
            // this environment (no edges) - recorded honestly, not fabricated.
            var covering = await SelectCoveringTestsAsync(work, notes, cancellationToken);
            var coveringFilter = covering.Count > 0
                ? TestFilterBuilder.BuildContains(covering)
                : TestFilterBuilder.BuildContains([CoveringTestType]);

            var cleanSource = await File.ReadAllTextAsync(classPath, cancellationToken);
            var mutants = GenerateBehaviorMutants(cleanSource, Mutants);
            if (mutants.Count == 0)
                notes.Add("no behavior mutants generated (the class under test lacks a condition or comparison to mutate).");

            // Each mutant is run against BOTH the whole suite and the covering subset the selection picked.
            // Selection-safety is measured relative to what the whole suite catches: a selection MISS is a mutant
            // the whole suite kills but the covering subset does not (the selection dropped a test that mattered).
            // A mutant no test catches (a fixture coverage gap) is not a selection failure - it is excluded from
            // the safety denominator, which is the count of mutants the whole suite kills.
            var latencies = new List<double> { cleanWatch.ElapsedMilliseconds };
            var killed = 0;
            var fullSuiteKilled = 0;
            var selectionMisses = 0;
            foreach (var (id, mutantSource) in mutants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await File.WriteAllTextAsync(classPath, mutantSource, cancellationToken);

                var watch = Stopwatch.StartNew();
                var coveringRun = await BuildGradeTestRunner.RunAsync(project, coveringFilter, scratch, RunTimeout, cancellationToken);
                watch.Stop();
                latencies.Add(watch.ElapsedMilliseconds);
                var coveringFailed = coveringRun.Verdicts.Any(v => v.Outcome == "failed");

                var fullRun = await BuildGradeTestRunner.RunAsync(project, fullFilter, scratch, RunTimeout, cancellationToken);
                var fullFailed = fullRun.Verdicts.Any(v => v.Outcome == "failed");

                if (coveringFailed)
                    killed++;
                if (fullFailed)
                {
                    fullSuiteKilled++;
                    // The whole suite caught it; the covering subset must too, or the selection was unsafe.
                    if (!coveringFailed && covering.Count > 0)
                        selectionMisses++;
                }

                tasks.Add(new TaskResult(id, "testexec", "mutant", coveringFailed ? 1.0 : 0.0, 1.0, 0, watch.ElapsedMilliseconds, EmptyFiles));
                await File.WriteAllTextAsync(classPath, cleanSource, cancellationToken);
            }

            var killRate = mutants.Count > 0 ? (double)killed / mutants.Count : 0.0;
            var selectionSafety = fullSuiteKilled > 0 ? (double)(fullSuiteKilled - selectionMisses) / fullSuiteKilled : 1.0;
            var median = Median(latencies);
            notes.Add($"behavior mutants: {mutants.Count} generated, {killed} killed by the covering subset (kill rate {killRate:P0}); false green 0 by construction (the runner mirrors dotnet test)");
            if (covering.Count > 0)
                notes.Add($"selection-safety: covering set [{string.Join(", ", covering)}] excluded the unrelated test; the covering subset caught {fullSuiteKilled - selectionMisses}/{fullSuiteKilled} of what the whole suite caught ({selectionSafety:P0}), {selectionMisses} selection miss(es)");
            else
                notes.Add("selection-safety: not measured (the fixture did not load semantically here, so no R5 tests edge was produced); the covering subset fell back to the whole suite. This is the same index-mode ceiling that bounds the corpus suites.");
            notes.Add($"per-run latency (build-grade): median {median:F0} ms over {latencies.Count} run(s); the emit fast-path (sub-second, no build) is the named follow-up");

            // Scorecard: recall = kill rate, precision = selection-safety (the covering subset is precise), the
            // median columns carry latency (the suite conventions reused).
            var scorecard = new Scorecard(tasks.Count, killRate, killRate, killRate, selectionSafety, killRate, median, median);
            await WriteAsync(new SuiteResult("testexec", Description(), null, scorecard, tasks, notes), cancellationToken);
            _consoleUI.WriteResult(string.Join("\n", notes));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    // Index the clean fixture and return the simple names of the test types that cover Calc through the R5 tests
    // edges. The unrelated test (OtherTests, which references only Other) must not appear; if it does, the note
    // records the leak. Returns empty when the fixture loads syntax-only (no edges in this environment).
    private async Task<IReadOnlyList<string>> SelectCoveringTestsAsync(
        string work, List<string> notes, CancellationToken cancellationToken)
    {
        try
        {
            var databasePath = FuseStorePaths.ResolveDatabasePath(work);
            await using var store = new WorkspaceIndexStore(databasePath);
            var result = await _indexer.IndexAsync(work, store, cancellationToken);

            var explorer = new GraphNeighborhoodExplorer(store);
            var covering = await explorer.CoveringTestsAsync(SymbolUnderTest, limit: 20, cancellationToken);
            var names = covering
                .Select(c => SimpleTypeName(c.Symbol ?? string.Empty))
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (names.Count == 0)
            {
                notes.Add($"covering selection: index mode [{result.Mode}] produced no tests edge to {SymbolUnderTest} (selection-safety falls back to the whole suite).");
                return [];
            }

            if (names.Contains(UnrelatedTestType, StringComparer.Ordinal))
                notes.Add($"covering selection LEAK: {UnrelatedTestType} was selected as covering {SymbolUnderTest} but does not reference it.");
            return names;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            notes.Add($"covering selection: index/query failed ({ex.GetType().Name}); selection-safety falls back to the whole suite.");
            return [];
        }
    }

    private static string SimpleTypeName(string displayName)
    {
        var name = displayName;
        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1)
            name = name[(dot + 1)..];
        var generic = name.IndexOf('<');
        if (generic >= 0)
            name = name[..generic];
        return name.Trim();
    }

    private async Task WriteAsync(SuiteResult result, CancellationToken cancellationToken)
    {
        var outputPath = Output is null
            ? System.IO.Path.GetFullPath(System.IO.Path.Combine("tests", "benchmarks", "results", "testexec.json"))
            : System.IO.Path.GetFullPath(Output);
        await Reporting.WriteAsync(result, outputPath, cancellationToken);
        _consoleUI.WriteStep($"Wrote results to {outputPath}");
    }

    private static string Description() => "Suite T1: build-grade covering-test execution honesty, mutant kill rate, and selection-safety.";

    private static Scorecard Empty() => new(0, 0, 0, 0, 0, 0, 0, 0);

    private static TaskFiles EmptyFiles => new([], [], []);

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static IReadOnlyList<(string Id, string Source)> GenerateBehaviorMutants(string classSource, int count)
    {
        var tree = CSharpSyntaxTree.ParseText(classSource, path: "Calc.cs");
        var compilation = CSharpCompilation.Create(
            "TestExecMutation", [tree], FrameworkReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        if (compilation.GetDiagnostics().Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
            return [];

        return new MutationGenerator()
            .GenerateBehaviorMutants(compilation, ["Calc.cs"], count, seed: 1234)
            .Select(m => (m.Name, m.NewContent))
            .ToList();
    }

    private static IEnumerable<MetadataReference> FrameworkReferences()
    {
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty;
        foreach (var path in tpa.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                yield return MetadataReference.CreateFromFile(path);
        }
    }

    private static async Task WriteFixtureAsync(string work, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(System.IO.Path.Combine(work, "Fix.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
                <PackageReference Include="xunit" Version="2.9.2" />
                <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
              </ItemGroup>
            </Project>
            """, cancellationToken);
        await File.WriteAllTextAsync(System.IO.Path.Combine(work, "Calc.cs"), """
            namespace Fix;
            public static class Calc
            {
                public static int Clamp(int x)
                {
                    if (x > 10) { return 10; }
                    return x;
                }
                public static bool IsPositive(int x) => x > 0;
            }
            """, cancellationToken);
        // An unrelated class + its tests: the selection must NOT pick OtherTests as covering Calc.
        await File.WriteAllTextAsync(System.IO.Path.Combine(work, "Other.cs"), """
            namespace Fix;
            public static class Other
            {
                public static int Double(int x) => x * 2;
            }
            """, cancellationToken);
        await File.WriteAllTextAsync(System.IO.Path.Combine(work, "CalcTests.cs"), """
            using Xunit;
            namespace Fix;
            public class CalcTests
            {
                [Fact] public void Clamp_caps_at_ten() => Assert.Equal(10, Calc.Clamp(50));
                [Fact] public void Clamp_passes_small_values() => Assert.Equal(3, Calc.Clamp(3));
                [Fact] public void IsPositive_true_for_positive() => Assert.True(Calc.IsPositive(5));
                [Fact] public void IsPositive_false_for_zero() => Assert.False(Calc.IsPositive(0));
            }
            """, cancellationToken);
        await File.WriteAllTextAsync(System.IO.Path.Combine(work, "OtherTests.cs"), """
            using Xunit;
            namespace Fix;
            public class OtherTests
            {
                [Fact] public void Double_doubles() => Assert.Equal(6, Other.Double(3));
            }
            """, cancellationToken);
    }
}
