using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Fuse.Semantics.Tests;

// T4 precondition spike (the descope hinge): can an AdhocWorkspace host and run a CodeFixProvider without the
// Microsoft.CodeAnalysis.Features package (which is not referenced), so apply_codefix can drive the repo's own
// analyzer-shipped fixes? This proves the mechanism end to end: a hand-authored provider produces a CodeAction, we
// pull its solution-changing operation, apply it, and confirm the document changed. If this passes, apply_codefix
// is buildable on the Workspaces abstraction alone (fix providers loaded from analyzer references by reflection).
public sealed class CodeFixHostingSpikeTests
{
    [Fact]
    public async Task AdhocWorkspace_can_host_and_apply_a_code_fix()
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var project = ProjectInfo.Create(
            projectId, VersionStamp.Default, "Fix", "Fix", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var docId = DocumentId.CreateNewId(projectId);
        var solution = workspace.CurrentSolution
            .AddProject(project)
            .AddDocument(docId, "C.cs", SourceText.From("class c { }"), filePath: "C.cs");
        var document = solution.GetDocument(docId)!;

        // A diagnostic on the class identifier 'c' (a stand-in for whatever an analyzer would flag).
        var root = await document.GetSyntaxRootAsync(CancellationToken.None);
        var identifier = root!.DescendantTokens().First(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == "c");
        var diagnostic = Diagnostic.Create(Descriptor, Location.Create(root.SyntaxTree, identifier.Span));

        // Host the provider: build a CodeFixContext, let it register its actions, then apply the first.
        var provider = new UppercaseClassNameFix();
        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None);
        await provider.RegisterCodeFixesAsync(context);

        Assert.NotEmpty(actions);

        var operations = await actions[0].GetOperationsAsync(CancellationToken.None);
        var applyChanges = operations.OfType<ApplyChangesOperation>().First();
        var changedDoc = applyChanges.ChangedSolution.GetDocument(docId)!;
        var changedText = (await changedDoc.GetTextAsync(CancellationToken.None)).ToString();

        // The fix renamed the class to start with an uppercase letter, proving the hosted fix's edit applied.
        Assert.Contains("class C", changedText);
        Assert.DoesNotContain("class c", changedText);
    }

    // RS2008 (analyzer release tracking) does not apply to a test-only spike descriptor.
#pragma warning disable RS2008
    private static readonly DiagnosticDescriptor Descriptor = new(
        "SPIKE001", "Class name should be PascalCase", "Class '{0}' should be PascalCase", "Naming",
        Microsoft.CodeAnalysis.DiagnosticSeverity.Warning, isEnabledByDefault: true);
#pragma warning restore RS2008

    // A minimal code fix: uppercase the first letter of the flagged class name. Not exported (the spike constructs
    // it directly); apply_codefix would instead discover [ExportCodeFixProvider] types in the analyzer references.
    private sealed class UppercaseClassNameFix : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ["SPIKE001"];

        public override FixAllProvider? GetFixAllProvider() => null;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken);
            var token = root!.FindToken(context.Span.Start);
            var oldName = token.Text;
            var newName = char.ToUpperInvariant(oldName[0]) + oldName[1..];
            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Rename to '{newName}'",
                    ct =>
                    {
                        var newRoot = root.ReplaceToken(token, SyntaxFactory.Identifier(token.LeadingTrivia, newName, token.TrailingTrivia));
                        return Task.FromResult(context.Document.WithSyntaxRoot(newRoot).Project.Solution);
                    },
                    equivalenceKey: "uppercase"),
                context.Diagnostics);
        }
    }
}
