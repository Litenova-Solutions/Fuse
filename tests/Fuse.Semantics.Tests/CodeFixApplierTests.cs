using System.Collections.Immutable;
using Fuse.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Fuse.Semantics.Tests;

// T4b: the apply-codefix core drives an analyzer's own fix across a document, verify-gated. Tested deterministically
// over an in-memory AdhocWorkspace with an in-test analyzer (flags lowercase class names) and its fix (uppercase
// them), so the whole run-analyzer -> apply-fix -> re-analyze -> verify loop is exercised without MSBuild.
public sealed class CodeFixApplierTests
{
    private static (Solution Solution, DocumentId DocId) SolutionWith(string source)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(
            projectId, VersionStamp.Default, "Fix", "Fix", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var docId = DocumentId.CreateNewId(projectId);
        var solution = workspace.CurrentSolution.AddProject(projectInfo)
            .AddDocument(docId, "C.cs", SourceText.From(source), filePath: "C.cs");
        return (solution, docId);
    }

    [Fact]
    public async Task Applies_the_fix_to_every_occurrence_and_verifies_clean()
    {
        var (solution, docId) = SolutionWith("namespace Fix;\nclass widget { }\nclass gadget { }");

        var result = await new CodeFixApplier().ApplyAsync(
            solution, docId, "FUSEFIX001",
            [new LowercaseClassAnalyzer()], [new UppercaseClassFix()], CancellationToken.None);

        Assert.True(result.Changed, result.Reason);
        Assert.Equal(2, result.Applied);            // both lowercase classes fixed
        Assert.Contains("class Widget", result.NewText);
        Assert.Contains("class Gadget", result.NewText);
        Assert.DoesNotContain("class widget", result.NewText);
    }

    [Fact]
    public async Task Abstains_when_no_provider_fixes_the_id()
    {
        var (solution, docId) = SolutionWith("namespace Fix;\nclass widget { }");
        var result = await new CodeFixApplier().ApplyAsync(
            solution, docId, "CS9999", [new LowercaseClassAnalyzer()], [new UppercaseClassFix()], CancellationToken.None);
        Assert.False(result.Changed);
        Assert.Contains("no code fix provider fixes", result.Reason);
    }

    [Fact]
    public async Task Abstains_when_the_diagnostic_is_absent()
    {
        var (solution, docId) = SolutionWith("namespace Fix;\nclass Widget { }");
        var result = await new CodeFixApplier().ApplyAsync(
            solution, docId, "FUSEFIX001", [new LowercaseClassAnalyzer()], [new UppercaseClassFix()], CancellationToken.None);
        Assert.False(result.Changed);
        Assert.Contains("no 'FUSEFIX001' diagnostic", result.Reason);
    }

#pragma warning disable RS1036, RS2008, RS1038, RS1041
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class LowercaseClassAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new(
            "FUSEFIX001", "Class name should be PascalCase", "Class '{0}' should be PascalCase", "Naming",
            Microsoft.CodeAnalysis.DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(ctx =>
            {
                var decl = (ClassDeclarationSyntax)ctx.Node;
                if (decl.Identifier.Text.Length > 0 && char.IsLower(decl.Identifier.Text[0]))
                    ctx.ReportDiagnostic(Diagnostic.Create(Rule, decl.Identifier.GetLocation(), decl.Identifier.Text));
            }, SyntaxKind.ClassDeclaration);
        }
    }

    private sealed class UppercaseClassFix : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ["FUSEFIX001"];

        public override FixAllProvider? GetFixAllProvider() => null;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken);
            var token = root!.FindToken(context.Span.Start);
            var upper = char.ToUpperInvariant(token.Text[0]) + token.Text[1..];
            context.RegisterCodeFix(
                CodeAction.Create($"Rename to '{upper}'", ct =>
                {
                    var newRoot = root.ReplaceToken(token, SyntaxFactory.Identifier(token.LeadingTrivia, upper, token.TrailingTrivia));
                    return Task.FromResult(context.Document.WithSyntaxRoot(newRoot).Project.Solution);
                }, equivalenceKey: "upper"),
                context.Diagnostics);
        }
    }
#pragma warning restore RS1036, RS2008, RS1038, RS1041
}
