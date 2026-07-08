using System.Collections.Immutable;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Fuse.Workspace.Tests;

// S4: the analyzer half of the check. ResidentAnalyzerRunner runs a project's analyzers against a compilation and
// returns the error/warning diagnostics scoped to one document. Tested with an in-memory compilation and an inline
// analyzer, so the run-and-filter behavior is pinned without a build capture.
public sealed class ResidentAnalyzerRunnerTests
{
    [Fact]
    public async Task Returns_only_the_analyzer_diagnostics_in_the_scoped_tree()
    {
        var treeA = CSharpSyntaxTree.ParseText("public class A { }", path: "A.cs");
        var treeB = CSharpSyntaxTree.ParseText("public class B { }", path: "B.cs");
        var compilation = CSharpCompilation.Create(
            "Test", [treeA, treeB],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diagnostics = await ResidentAnalyzerRunner.DiagnosticsForTreeAsync(
            compilation, [new ClassFlagAnalyzer()], options: null, treeA, CancellationToken.None);

        // Both files declare a class, but only A.cs is in scope, so exactly one analyzer diagnostic is returned.
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("FUSE001", diagnostic.Id);
        Assert.Equal("A.cs", diagnostic.Location.SourceTree?.FilePath);
    }

    [Fact]
    public async Task An_empty_analyzer_set_returns_nothing()
    {
        var tree = CSharpSyntaxTree.ParseText("public class A { }", path: "A.cs");
        var compilation = CSharpCompilation.Create(
            "Test", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diagnostics = await ResidentAnalyzerRunner.DiagnosticsForTreeAsync(
            compilation, ImmutableArray<DiagnosticAnalyzer>.Empty, options: null, tree, CancellationToken.None);

        Assert.Empty(diagnostics);
    }

    // Flags every class declaration with a warning, so a test compilation deterministically produces analyzer
    // diagnostics without depending on a third-party analyzer package. RS1036 (release-tracking for shipped
    // analyzers) does not apply to a test-only fixture analyzer.
#pragma warning disable RS1036, RS2008
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class ClassFlagAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new(
            "FUSE001", "Class declared", "Class '{0}' declared", "Test",
            DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(
                ctx =>
                {
                    var declaration = (ClassDeclarationSyntax)ctx.Node;
                    ctx.ReportDiagnostic(Diagnostic.Create(Rule, declaration.Identifier.GetLocation(), declaration.Identifier.Text));
                },
                SyntaxKind.ClassDeclaration);
        }
    }
#pragma warning restore RS1036, RS2008
}
