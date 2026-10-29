using System.Collections.Concurrent;
using System.Collections.Immutable;
using Fuse.Check;
using Fuse.Check.Model;
using Fuse.Graph;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Tests.Engine;

/// <summary>
///     HEAD's finished baseline results are kept on disk by what they depend on, so the first check after an engine
///     restart reads them instead of binding HEAD again. Anything from another commit or configuration, anything
///     unreadable, and anything unfinished is computed again.
/// </summary>
public class BaselineStoreTests
{
    [Fact]
    public async Task Stored_baseline_errors_survive_an_engine_restart()
    {
        using var repo = FixtureRepo.CreateStandard();
        repo.Replace("Lib/Calc.cs", "a * b;", "a * existingError;");
        repo.Commit("an error at HEAD");
        repo.Replace("Lib/Calc.cs", "a + b;", "a + anotherError;");
        var cancellationToken = TestContext.Current.CancellationToken;

        CheckResult first;
        using (var workspace = new RepoWorkspace(repo.Root, _ => { }))
        {
            await workspace.InitializeAsync(cancellationToken);
            first = await CheckAsync(workspace, repo, "Lib/Calc.cs", cancellationToken);
        }

        Assert.Contains("anotherError", Assert.Single(first.Errors).Error.Message, StringComparison.Ordinal);
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(repo.Root.StateDirectory, "baseline"), "*.json", SearchOption.AllDirectories));

        CheckResult second;
        var log = new ConcurrentQueue<string>();
        using (var workspace = new RepoWorkspace(repo.Root, log.Enqueue))
        {
            await workspace.InitializeAsync(cancellationToken);
            second = await CheckAsync(workspace, repo, "Lib/Calc.cs", cancellationToken);
        }

        Assert.Equal(first.Errors.Select(e => e.Error), second.Errors.Select(e => e.Error));
        Assert.Contains(log, l => l.Contains("stored result(s) read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_entry_from_another_commit_is_ignored()
    {
        using var repo = FixtureRepo.CreateStandard();
        repo.Replace("Lib/Calc.cs", "a * b;", "a * existingError;");
        repo.Commit("an error at HEAD");
        repo.Replace("Lib/Calc.cs", "a + b;", "a + anotherError;");
        var cancellationToken = TestContext.Current.CancellationToken;
        using (var workspace = new RepoWorkspace(repo.Root, _ => { }))
        {
            await workspace.InitializeAsync(cancellationToken);
            await CheckAsync(workspace, repo, "Lib/Calc.cs", cancellationToken);
        }

        repo.Commit("another error at HEAD");
        repo.Replace("Lib/Calc.cs", "a * existingError;", "a * thirdError;");

        CheckResult second;
        var log = new ConcurrentQueue<string>();
        using (var workspace = new RepoWorkspace(repo.Root, log.Enqueue))
        {
            await workspace.InitializeAsync(cancellationToken);
            second = await CheckAsync(workspace, repo, "Lib/Calc.cs", cancellationToken);
        }

        Assert.Contains("thirdError", Assert.Single(second.Errors).Error.Message, StringComparison.Ordinal);
        Assert.Contains(log, l => l.Contains("0 stored result(s) read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_entry_from_another_configuration_is_ignored()
    {
        using var repo = FixtureRepo.CreateStandard();
        repo.Replace("Strict/Thing.cs", "public static int Value() => 1;", "public static int Value() => new int[0].Length + missing;");
        var cancellationToken = TestContext.Current.CancellationToken;
        using (var workspace = new RepoWorkspace(repo.Root, _ => { }))
        {
            await workspace.InitializeAsync(cancellationToken);
            var first = await CheckAsync(workspace, repo, "Strict/Thing.cs", cancellationToken);
            Assert.Equal(["CA1825", "CS0103"], first.Errors.Select(e => e.Error.Id).Order(StringComparer.Ordinal));
        }

        repo.Replace("Strict/.editorconfig", "dotnet_diagnostic.CA1825.severity = error", "dotnet_diagnostic.CA1825.severity = none");

        CheckResult second;
        var log = new ConcurrentQueue<string>();
        using (var workspace = new RepoWorkspace(repo.Root, log.Enqueue))
        {
            await workspace.InitializeAsync(cancellationToken);
            second = await CheckAsync(workspace, repo, "Strict/Thing.cs", cancellationToken);
        }

        Assert.Equal("CS0103", Assert.Single(second.Errors).Error.Id);
        Assert.Contains(log, l => l.Contains("0 stored result(s) read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unreadable_entry_still_yields_a_correct_check()
    {
        using var repo = FixtureRepo.CreateStandard();
        repo.Replace("Lib/Calc.cs", "a * b;", "a * existingError;");
        repo.Commit("an error at HEAD");
        repo.Replace("Lib/Calc.cs", "a + b;", "a + anotherError;");
        var cancellationToken = TestContext.Current.CancellationToken;
        using (var workspace = new RepoWorkspace(repo.Root, _ => { }))
        {
            await workspace.InitializeAsync(cancellationToken);
            await CheckAsync(workspace, repo, "Lib/Calc.cs", cancellationToken);
        }

        var stored = Directory.EnumerateFiles(Path.Combine(repo.Root.StateDirectory, "baseline"), "*.json", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(stored);
        foreach (var file in stored)
            File.WriteAllText(file, "not json{");

        CheckResult second;
        using (var workspace = new RepoWorkspace(repo.Root, _ => { }))
        {
            await workspace.InitializeAsync(cancellationToken);
            second = await CheckAsync(workspace, repo, "Lib/Calc.cs", cancellationToken);
        }

        Assert.Contains("anotherError", Assert.Single(second.Errors).Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unfinished_baseline_is_never_stored()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var cancellationToken = TestContext.Current.CancellationToken;
        var project = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "P", "P", LanguageNames.CSharp)
            .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)])
            .WithAnalyzerReferences([new AnalyzerImageReference([new CheapAnalyzer()])]);
        using var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.AddProject(project);
        solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), "C1.cs", "public class C1 { }\n", filePath: repo.Full("C1.cs"));
        var graph = await RepoGraph.EvaluateAsync(repo.Root, cancellationToken);
        var head = FixtureRepo.Run(repo.Root.Path, "git", "rev-parse", "HEAD").Trim();
        var collector = new DiagnosticCollector(repo.Root, () => 0, _ => { });
        using var passed = new CancellationTokenSource();
        await passed.CancelAsync();

        collector.Begin(new AnalyzerRun(CompilationWide: true, passed.Token), reuse: false);
        var key = BaselineStore.KeyFor(head, solution, graph, repo.Root);
        Assert.NotNull(key);
        var (errors, finished) = await collector.ForBaselineFileAsync(solution, 0, repo.Root.PathOf(repo.Full("C1.cs")), cancellationToken, key);

        Assert.False(finished);
        Assert.Empty(errors);
        Assert.False(Directory.Exists(Path.Combine(repo.Root.StateDirectory, "baseline")));
    }

    private static Task<CheckResult> CheckAsync(RepoWorkspace workspace, FixtureRepo repo, string relative, CancellationToken cancellationToken) =>
        new Checker(workspace, new SessionEdits(workspace.Root, null)).CheckAsync(
            new CheckScope.Files([repo.PathOf(relative)]), PhaseTimes.None, cancellationToken);

    /// <summary>Reports every class, with no compilation start.</summary>
#pragma warning disable RS1001, RS1025, RS1026, RS2008
    private sealed class CheapAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new("CHEAP1", "Cheap", "class {0}", "Test", DiagnosticSeverity.Error, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSymbolAction(s => s.ReportDiagnostic(Diagnostic.Create(Rule, s.Symbol.Locations[0], s.Symbol.Name)), SymbolKind.NamedType);
        }
    }
#pragma warning restore RS1001, RS1025, RS1026, RS2008
}
