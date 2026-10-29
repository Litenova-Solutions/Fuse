using System.Collections.Immutable;
using Fuse.Check;
using Fuse.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Tests.Unit;

/// <summary>
///     A compilation-wide analyzer runs again after every edit, even one that leaves the declarations alone.
///     Classifying it from one compilation cannot prove a body pattern absent today stays irrelevant tomorrow.
/// </summary>
public class CompilationWideRerunTests
{
    [Fact]
    public async Task A_body_edit_that_adds_the_pattern_reports_the_new_error()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var solution = SolutionWith(repo);
        var collector = new DiagnosticCollector(repo.Root, () => 0, _ => { });
        var path = repo.Root.PathOf(repo.Full("C0.cs"));
        var cancellationToken = TestContext.Current.CancellationToken;

        collector.Begin(AnalyzerRun.Full, reuse: false);
        var before = await collector.ForFileAsync(solution, path, cancellationToken);

        Assert.All(before, e => Assert.NotEqual("PATT1", e.Id));

        var c0 = solution.Projects.Single().Documents.Single(d => d.Name == "C0.cs").Id;
        var edited = solution.WithDocumentText(c0, Microsoft.CodeAnalysis.Text.SourceText.From("public class C0 { public int F() { /* MARKER */ return 1; } }\n"));
        collector.Begin(AnalyzerRun.Full, reuse: false);
        var after = await collector.ForFileAsync(edited, path, cancellationToken);

        Assert.Contains(after, e => e.Id == "PATT1");
    }

    private static Solution SolutionWith(FixtureRepo repo)
    {
        var project = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "P", "P", LanguageNames.CSharp)
            .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)])
            .WithAnalyzerReferences([new AnalyzerImageReference([new BodyPatternAnalyzer()])]);
        using var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.AddProject(project);
        solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), "C0.cs", "public class C0 { public int F() { return 1; } }\n", filePath: repo.Full("C0.cs"));
        solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), "C1.cs", "public class C1 { }\n", filePath: repo.Full("C1.cs"));
        return solution;
    }

    /// <summary>Reports every method whose body names the marker, after a compilation start slow enough to plan it whole.</summary>
#pragma warning disable RS1001, RS1025, RS1026, RS2008
    private sealed class BodyPatternAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new("PATT1", "Pattern", "body names {0}", "Test", DiagnosticSeverity.Error, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                Thread.Sleep(AnalyzerPlan.CompilationWideStart * 1.5);
                start.RegisterSymbolAction(
                    s =>
                    {
                        if (s.Symbol.DeclaringSyntaxReferences.Any(r => r.GetSyntax(s.CancellationToken).ToString().Contains("MARKER", StringComparison.Ordinal)))
                            s.ReportDiagnostic(Diagnostic.Create(Rule, s.Symbol.Locations[0], "MARKER"));
                    },
                    SymbolKind.Method);
            });
        }
    }
#pragma warning restore RS1001, RS1025, RS1026, RS2008
}
