using System.Collections.Immutable;
using Fuse.Check;
using Fuse.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Tests.Unit;

/// <summary>
///     A compilation-wide analyzer that reads only declarations runs once per declarations: a body edit maps its kept
///     errors to the new compilation instead of running it again. One that reads bodies runs again after every edit.
/// </summary>
public class DeclarationOnlyTests
{
    [Fact]
    public async Task A_declaration_only_analyzer_is_not_run_again_after_a_body_edit()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var slow = new DeclAnalyzer();
        var solution = SolutionWith(repo, slow, ("C0.cs", "public class C0 { }\n"), ("C1.cs", "public class C1\n{\n    public int M()\n    {\n        return 1;\n    }\n}\n\npublic class Widget1 { }\n"));
        var lines = new List<string>();
        var collector = new DiagnosticCollector(repo.Root, () => 0, lines.Add);
        var path = repo.Root.PathOf(repo.Full("C1.cs"));

        collector.Begin(AnalyzerRun.Full, reuse: false);
        var first = await collector.ForFileAsync(solution, path, TestContext.Current.CancellationToken);
        var afterFirst = slow.Starts;
        Assert.Equal("DECL1", Assert.Single(first).Id);
        Assert.Equal(9, Assert.Single(first).Line);
        Assert.Contains(lines, l => l.Contains("declaration-only:", StringComparison.Ordinal));

        var document = solution.GetDocumentIdsWithFilePath(repo.Full("C1.cs")).Single();
        solution = solution.WithDocumentText(document, Microsoft.CodeAnalysis.Text.SourceText.From("public class C1\n{\n    public int M()\n    {\n        var a = 1;\n        var b = 2;\n        return a + b;\n    }\n}\n\npublic class Widget1 { }\n"));
        collector.Begin(AnalyzerRun.Full, reuse: false);
        var second = await collector.ForFileAsync(solution, path, TestContext.Current.CancellationToken);

        var error = Assert.Single(second);
        Assert.Equal("DECL1", error.Id);
        Assert.Equal(11, error.Line);
        Assert.Equal(afterFirst, slow.Starts);
    }

    [Fact]
    public async Task A_body_reading_analyzer_runs_again_after_a_body_edit()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var slow = new BodyAnalyzer();
        var solution = SolutionWith(repo, slow, ("C0.cs", "public class C0\n{\n    public static void Target() { }\n\n    public static void Caller()\n    {\n        Target();\n    }\n}\n"));
        var collector = new DiagnosticCollector(repo.Root, () => 0, _ => { });
        var path = repo.Root.PathOf(repo.Full("C0.cs"));

        collector.Begin(AnalyzerRun.Full, reuse: false);
        var first = await collector.ForFileAsync(solution, path, TestContext.Current.CancellationToken);
        var afterFirst = slow.Starts;
        Assert.Equal("BODY1", Assert.Single(first).Id);

        var document = solution.GetDocumentIdsWithFilePath(repo.Full("C0.cs")).Single();
        solution = solution.WithDocumentText(document, Microsoft.CodeAnalysis.Text.SourceText.From("public class C0\n{\n    public static void Target() { }\n\n    public static void Caller()\n    {\n        Target();\n        Target();\n    }\n}\n"));
        collector.Begin(AnalyzerRun.Full, reuse: false);
        var second = await collector.ForFileAsync(solution, path, TestContext.Current.CancellationToken);

        Assert.Equal(2, second.Count);
        Assert.All(second, e => Assert.Equal("BODY1", e.Id));
        Assert.True(slow.Starts > afterFirst, "a body-reading analyzer runs again after a body edit");
    }

    [Fact]
    public async Task A_declaration_edit_reruns_a_declaration_only_analyzer()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var slow = new DeclAnalyzer();
        var solution = SolutionWith(repo, slow, ("C1.cs", "public class Widget1 { }\n"));
        var collector = new DiagnosticCollector(repo.Root, () => 0, _ => { });
        var path = repo.Root.PathOf(repo.Full("C1.cs"));

        collector.Begin(AnalyzerRun.Full, reuse: false);
        var first = await collector.ForFileAsync(solution, path, TestContext.Current.CancellationToken);
        var afterFirst = slow.Starts;
        Assert.Contains("Widget1", Assert.Single(first).Message, StringComparison.Ordinal);

        var document = solution.GetDocumentIdsWithFilePath(repo.Full("C1.cs")).Single();
        solution = solution.WithDocumentText(document, Microsoft.CodeAnalysis.Text.SourceText.From("public class Widget2 { }\n"));
        collector.Begin(AnalyzerRun.Full, reuse: false);
        var second = await collector.ForFileAsync(solution, path, TestContext.Current.CancellationToken);

        Assert.Contains("Widget2", Assert.Single(second).Message, StringComparison.Ordinal);
        Assert.True(slow.Starts > afterFirst, "a declaration edit reruns a declaration-only analyzer");
    }

    [Fact]
    public async Task An_analyzer_whose_result_differs_without_bodies_stays_body_reading()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var slow = new CountAnalyzer();
        var solution = SolutionWith(repo, slow, ("C0.cs", "public class C0\n{\n    public static void Caller()\n    {\n        Helper();\n        Helper();\n    }\n\n    public static void Helper() { }\n}\n"));
        var lines = new List<string>();
        var collector = new DiagnosticCollector(repo.Root, () => 0, lines.Add);
        var path = repo.Root.PathOf(repo.Full("C0.cs"));

        collector.Begin(AnalyzerRun.Full, reuse: false);
        var first = await collector.ForFileAsync(solution, path, TestContext.Current.CancellationToken);
        var afterFirst = slow.Starts;
        Assert.Contains("2 calls", Assert.Single(first).Message, StringComparison.Ordinal);
        var plan = Assert.Single(lines);
        Assert.Contains("compilation-wide", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("declaration-only", plan, StringComparison.Ordinal);

        var document = solution.GetDocumentIdsWithFilePath(repo.Full("C0.cs")).Single();
        solution = solution.WithDocumentText(document, Microsoft.CodeAnalysis.Text.SourceText.From("public class C0\n{\n    public static void Caller()\n    {\n        Helper();\n        Helper();\n        Helper();\n    }\n\n    public static void Helper() { }\n}\n"));
        collector.Begin(AnalyzerRun.Full, reuse: false);
        var second = await collector.ForFileAsync(solution, path, TestContext.Current.CancellationToken);

        Assert.Contains("3 calls", Assert.Single(second).Message, StringComparison.Ordinal);
        Assert.True(slow.Starts > afterFirst, "an analyzer that reads bodies runs again after a body edit");
    }

    private static Solution SolutionWith(FixtureRepo repo, DiagnosticAnalyzer analyzer, params (string Name, string Text)[] files)
    {
        var project = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "P", "P", LanguageNames.CSharp)
            .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)])
            .WithAnalyzerReferences([new AnalyzerImageReference([analyzer])]);
        using var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.AddProject(project);
        foreach (var (name, text) in files)
            solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), name, text, filePath: repo.Full(name));
        return solution;
    }

    /// <summary>Reports every class whose name starts with Widget, after a compilation start past the compilation-wide threshold.</summary>
#pragma warning disable RS1001, RS1025, RS1026, RS2008
    private sealed class DeclAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new("DECL1", "Declaration", "class {0}", "Test", DiagnosticSeverity.Error, isEnabledByDefault: true);
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

    /// <summary>Reports every call of a method named Target, after a compilation start past the compilation-wide threshold.</summary>
    private sealed class BodyAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new("BODY1", "Body", "call of {0}", "Test", DiagnosticSeverity.Error, isEnabledByDefault: true);
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
                start.RegisterOperationAction(
                    operation =>
                    {
                        if (operation.Operation is Microsoft.CodeAnalysis.Operations.IInvocationOperation invocation && invocation.TargetMethod.Name == "Target")
                            operation.ReportDiagnostic(Diagnostic.Create(Rule, operation.Operation.Syntax.GetLocation(), invocation.TargetMethod.Name));
                    },
                    OperationKind.Invocation);
            });
        }
    }

    /// <summary>Reports each class with how many calls its methods hold, after a compilation start past the compilation-wide threshold.</summary>
    private sealed class CountAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new("COUNT1", "Count", "type {0} has {1} calls", "Test", DiagnosticSeverity.Error, isEnabledByDefault: true);
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
                        if (s.Symbol is not INamedTypeSymbol type)
                            return;
                        var calls = type.DeclaringSyntaxReferences
                            .SelectMany(r => r.GetSyntax().DescendantNodes().OfType<InvocationExpressionSyntax>())
                            .Count();
                        s.ReportDiagnostic(Diagnostic.Create(Rule, s.Symbol.Locations[0], s.Symbol.Name, calls));
                    },
                    SymbolKind.NamedType);
            });
        }
    }
#pragma warning restore RS1001, RS1025, RS1026, RS2008
}
