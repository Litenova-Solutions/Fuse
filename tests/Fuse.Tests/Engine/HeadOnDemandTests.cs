using System.Collections.Immutable;
using Fuse.Check;
using Fuse.Graph;
using Fuse.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Tests.Engine;

/// <summary>
///     HEAD's errors come from the disk store: the warm fills it after HEAD moves and releases the baseline
///     compilation it built, a check builds a baseline compilation only on a store miss, and what a check built
///     over HEAD is released when the check ends, so the next check reads the store again.
/// </summary>
public class HeadOnDemandTests
{
    [Fact]
    public async Task The_warm_fills_the_store_so_a_fresh_collector_reads_HEAD_without_binding_it()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var analyzer = new CountingAnalyzer();
        var project = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "P", "P", LanguageNames.CSharp)
            .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)])
            .WithAnalyzerReferences([new AnalyzerImageReference([analyzer])]);
        using var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.AddProject(project);
        solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), "C0.cs", "public class C0 { }\n", filePath: repo.Full("C0.cs"));
        solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), "C1.cs", "public class Widget1 { }\n", filePath: repo.Full("C1.cs"));
        var cancellationToken = TestContext.Current.CancellationToken;
        var graph = await RepoGraph.EvaluateAsync(repo.Root, cancellationToken);
        var head = FixtureRepo.Run(repo.Root.Path, "git", "rev-parse", "HEAD").Trim();
        var key = BaselineStore.KeyFor(head, solution, graph, repo.Root);
        Assert.NotNull(key);

        var filling = new DiagnosticCollector(repo.Root, () => 0, _ => { });
        filling.Begin(AnalyzerRun.Full, reuse: false);
        await filling.FillBaselineStoreAsync(solution.Projects.Single(), 0, cancellationToken, key);

        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(repo.Root.SharedStateDirectory, "baseline"), "*.json", SearchOption.AllDirectories));
        var bound = analyzer.Reports;
        Assert.True(bound > 0);

        var reading = new DiagnosticCollector(repo.Root, () => 0, _ => { });
        reading.Begin(AnalyzerRun.Full, reuse: false);
        var (errors, finished) = await reading.ForBaselineFileAsync(solution, 0, repo.Root.PathOf(repo.Full("C1.cs")), cancellationToken, key);

        Assert.True(finished);
        Assert.Equal("COUNT1", Assert.Single(errors).Id);
        Assert.Equal(bound, analyzer.Reports);

        Assert.False(await reading.FillBaselineStoreAsync(solution.Projects.Single(), 0, cancellationToken, key));
        Assert.Equal(bound, analyzer.Reports);
    }

    [Fact]
    public async Task A_warm_after_HEAD_moves_leaves_HEAD_errors_out_of_the_next_check()
    {
        await using var engine = await InProcessEngine.StartAsync();
        Assert.Empty((await engine.CheckAsync("Lib/Calc.cs")).Errors);
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * existingError;");
        engine.Repo.Commit("an error at HEAD");
        await engine.SyncAsync("Lib/Calc.cs");

        var lib = engine.Workspace.Graph.Projects.Single(p => p.Name == "Lib");
        await engine.Checker.WarmBaselineAsync(lib, TestContext.Current.CancellationToken);

        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(engine.Repo.Root.SharedStateDirectory, "baseline"), "*.json", SearchOption.AllDirectories));

        engine.Repo.Replace("Lib/Calc.cs", "a * existingError;", "a * anotherError;");
        var report = await engine.CheckAsync("Lib/Calc.cs");

        Assert.Contains("anotherError", Assert.Single(report.Errors).Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_next_check_reads_HEAD_from_the_store_the_last_check_wrote()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * existingError;");
        engine.Repo.Commit("an error at HEAD");
        engine.Repo.Replace("Lib/Calc.cs", "a * existingError;", "a * anotherError;");

        Assert.Contains("anotherError", Assert.Single((await engine.CheckAsync("Lib/Calc.cs")).Errors).Error.Message, StringComparison.Ordinal);
        Assert.Contains("anotherError", Assert.Single((await engine.CheckAsync("Lib/Calc.cs")).Errors).Error.Message, StringComparison.Ordinal);

        Assert.Contains(engine.Log, l => l.Contains("1 stored result(s) read", StringComparison.Ordinal));
    }

    /// <summary>Reports every class, and counts how many classes it reported, so a test can tell binding from reading the store.</summary>
#pragma warning disable RS1001, RS1025, RS1026, RS2008
    private sealed class CountingAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new("COUNT1", "Counted", "class {0}", "Test", DiagnosticSeverity.Error, isEnabledByDefault: true);
        private int _reports;

        public int Reports => Volatile.Read(ref _reports);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSymbolAction(
                s =>
                {
                    Interlocked.Increment(ref _reports);
                    s.ReportDiagnostic(Diagnostic.Create(Rule, s.Symbol.Locations[0], s.Symbol.Name));
                },
                SymbolKind.NamedType);
        }
    }
#pragma warning restore RS1001, RS1025, RS1026, RS2008
}
