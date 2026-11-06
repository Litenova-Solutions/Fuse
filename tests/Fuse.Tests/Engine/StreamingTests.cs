using System.Collections.Immutable;
using Fuse.Check;
using Fuse.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Tests.Engine;

/// <summary>
///     Past 500 candidates a check binds whole projects by streaming: it binds a document, keeps its errors and
///     drops its model, while the compilation-wide analyzers keep their one run over the whole compilation.
/// </summary>
public class StreamingTests
{
    [Fact]
    public async Task Past_five_hundred_candidates_the_streamed_projects_keep_every_files_errors()
    {
        await using var engine = await StartWithAsync(
            [.. Enumerable.Range(0, 510).Select(i => ($"Lib/Generated/Part{i}.cs", $"namespace Lib;\n\npublic static class Part{i}\n{{\n    public static int Get() => new Calc().Add({i}, 1);\n}}\n"))]);
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");

        var report = await engine.CheckAsync("Lib/Calc.cs");

        Assert.True(report.CheckedWholeProjects);
        Assert.True(report.ErrorFileCount >= 510, $"expected at least 510 files with errors, got {report.ErrorFileCount}");
        Assert.Contains(report.Errors, e => e.Error.Path.StartsWith("Lib/Generated/Part", StringComparison.Ordinal));
        Assert.All(report.Errors, e => Assert.Equal("CS0117", e.Error.Id));
    }

    [Fact]
    public async Task A_streamed_project_keeps_the_errors_of_every_document()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var project = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "P", "P", LanguageNames.CSharp)
            .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        using var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.AddProject(project);
        for (var i = 0; i < 3; i++)
            solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), $"C{i}.cs", $"public class C{i} {{ public int F() => missing{i}; }}\n", filePath: repo.Full($"C{i}.cs"));
        var collector = new DiagnosticCollector(repo.Root, () => 0, _ => { });
        collector.Begin(AnalyzerRun.Full, reuse: false);

        var errors = await collector.ForProjectAsync(solution.Projects.Single(), TestContext.Current.CancellationToken);

        Assert.Equal(3, errors.Count);
        Assert.All(errors, e => Assert.Equal("CS0103", e.Id));
        Assert.Equal(["C0.cs", "C1.cs", "C2.cs"], errors.Select(e => e.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_streamed_project_shares_the_one_compilation_wide_run()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var slow = new SlowDeclAnalyzer();
        var project = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "P", "P", LanguageNames.CSharp)
            .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)])
            .WithAnalyzerReferences([new AnalyzerImageReference([slow])]);
        using var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.AddProject(project);
        solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), "C0.cs", "public class C0 { }\n", filePath: repo.Full("C0.cs"));
        solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), "C1.cs", "public class Widget1 { }\n", filePath: repo.Full("C1.cs"));
        solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), "C2.cs", "public class Widget2 { }\n", filePath: repo.Full("C2.cs"));
        var collector = new DiagnosticCollector(repo.Root, () => 0, _ => { });
        var cancellationToken = TestContext.Current.CancellationToken;

        collector.Begin(AnalyzerRun.Full, reuse: false);
        Assert.True(await collector.WarmBaselineProjectAsync(solution.Projects.Single(), 0, cancellationToken));
        var afterWarm = slow.Starts;

        var errors = await collector.ForProjectAsync(solution.Projects.Single(), cancellationToken);

        Assert.Equal(afterWarm, slow.Starts);
        Assert.Equal(["C1.cs", "C2.cs"], errors.Where(e => e.Id == "DECL2").Select(e => e.Path).Order(StringComparer.Ordinal));
    }

    private static async Task<InProcessEngine> StartWithAsync(params (string Path, string Content)[] files)
    {
        var repo = FixtureRepo.CreateStandard();
        foreach (var (path, content) in files)
            repo.Write(path, content);
        repo.Commit("fixture files");
        return await InProcessEngine.StartAsync(repo);
    }

    /// <summary>Reports every class whose name starts with Widget, after a compilation start past the compilation-wide threshold.</summary>
#pragma warning disable RS1001, RS1025, RS1026, RS2008
    private sealed class SlowDeclAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new("DECL2", "Declaration", "class {0}", "Test", DiagnosticSeverity.Error, isEnabledByDefault: true);
        private int _starts;

        public int Starts => Volatile.Read(ref _starts);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                Interlocked.Increment(ref _starts);
                Thread.Sleep(AnalyzerPlan.CompilationWideStart + TimeSpan.FromMilliseconds(100));
                start.RegisterSymbolAction(
                    s =>
                    {
                        if (s.Symbol.Name.StartsWith("Widget", StringComparison.Ordinal))
                            s.ReportDiagnostic(Diagnostic.Create(Rule, s.Symbol.Locations[0], s.Symbol.Name));
                    },
                    SymbolKind.NamedType);
            });
        }
    }
#pragma warning restore RS1001, RS1025, RS1026, RS2008
}
