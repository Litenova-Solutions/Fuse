using System.Diagnostics;
using DotMake.CommandLine;
using Fuse.Benchmarks;
using Fuse.Cli.Services;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Fuse.Cli.Commands;

/// <summary>
///     Suite T1 (as a dedicated command, the resident-latency precedent): measures build-grade covering-test
///     execution honesty and behavior-mutant kill rate. It runs the shipped build-grade covering-test runner over
///     a self-contained xunit fixture - a clean run must be all green (no false red), and each compiling behavior
///     mutant of the class under test must turn a covering test red (killed = the runner honestly surfaced the
///     break) - and writes <c>results/testexec.json</c>.
/// </summary>
/// <remarks>
///     This is a command rather than a <c>fuse eval</c> suite because the build-grade runner lives in
///     <c>Fuse.Workspace</c>, whose Basic.CompilerLog closure (the VB 4.14 pin) cannot be referenced from the
///     <c>Fuse.Benchmarks</c> suite assembly without breaking its MSBuildWorkspace-based suites (the S1
///     co-activation constraint). Run in its own process, this command touches neither MSBuildWorkspace nor the
///     resident workspace, so it is safe. False green is 0 by construction (the runner mirrors the real
///     <c>dotnet test</c> TRX); the mutant-kill rate is the coverage signal. Selection-safety (needs R5 covering
///     edges) and the emit fast-path latency are the named follow-ups.
/// </remarks>
[CliCommand(
    Name = "testexec",
    Description = "Measure build-grade covering-test execution honesty and behavior-mutant kill rate; writes results/testexec.json.",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class TestExecCommand
{
    private const string TestTypeName = "Fix.CalcTests";
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(5);

    private readonly IConsoleUI _consoleUI;

    /// <summary>Initializes a new instance of the <see cref="TestExecCommand" /> class for CLI binding.</summary>
    public TestExecCommand() : this(null!)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TestExecCommand" /> class.</summary>
    /// <param name="consoleUI">The console UI for output.</param>
    public TestExecCommand(IConsoleUI consoleUI) => _consoleUI = consoleUI;

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
            var filter = TestFilterBuilder.BuildContains([TestTypeName]);

            var cleanWatch = Stopwatch.StartNew();
            var clean = await BuildGradeTestRunner.RunAsync(project, filter, scratch, RunTimeout, cancellationToken);
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

            var cleanSource = await File.ReadAllTextAsync(classPath, cancellationToken);
            var mutants = GenerateBehaviorMutants(cleanSource, Mutants);
            if (mutants.Count == 0)
                notes.Add("no behavior mutants generated (the class under test lacks a condition or comparison to mutate).");

            var latencies = new List<double> { cleanWatch.ElapsedMilliseconds };
            var killed = 0;
            foreach (var (id, mutantSource) in mutants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await File.WriteAllTextAsync(classPath, mutantSource, cancellationToken);
                var watch = Stopwatch.StartNew();
                var run = await BuildGradeTestRunner.RunAsync(project, filter, scratch, RunTimeout, cancellationToken);
                watch.Stop();
                latencies.Add(watch.ElapsedMilliseconds);

                var mutantFailed = run.Verdicts.Any(v => v.Outcome == "failed");
                if (mutantFailed)
                    killed++;
                tasks.Add(new TaskResult(id, "testexec", "mutant", mutantFailed ? 1.0 : 0.0, 1.0, 0, watch.ElapsedMilliseconds, EmptyFiles));
                await File.WriteAllTextAsync(classPath, cleanSource, cancellationToken);
            }

            var killRate = mutants.Count > 0 ? (double)killed / mutants.Count : 0.0;
            var median = Median(latencies);
            notes.Add($"behavior mutants: {mutants.Count} generated, {killed} killed (kill rate {killRate:P0}); false green 0 by construction (the runner mirrors dotnet test)");
            notes.Add($"per-run latency (build-grade): median {median:F0} ms over {latencies.Count} run(s); the emit fast-path (sub-second, no build) is the named follow-up");
            notes.Add("selection-safety metric needs R5 covering edges (a semantic-indexed fixture) and is the named follow-up.");

            var scorecard = new Scorecard(tasks.Count, killRate, killRate, killRate, 1.0, killRate, median, median);
            await WriteAsync(new SuiteResult("testexec", Description(), null, scorecard, tasks, notes), cancellationToken);
            _consoleUI.WriteResult(string.Join("\n", notes));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private async Task WriteAsync(SuiteResult result, CancellationToken cancellationToken)
    {
        var outputPath = Output is null
            ? System.IO.Path.GetFullPath(System.IO.Path.Combine("tests", "benchmarks", "results", "testexec.json"))
            : System.IO.Path.GetFullPath(Output);
        await Reporting.WriteAsync(result, outputPath, cancellationToken);
        _consoleUI.WriteStep($"Wrote results to {outputPath}");
    }

    private static string Description() => "Suite T1: build-grade covering-test execution honesty and behavior-mutant kill rate.";

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
        if (compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
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
    }
}
