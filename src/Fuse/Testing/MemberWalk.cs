using System.Collections.Immutable;
using Fuse.Changes;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Testing.Model;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Fuse.Testing;

/// <summary>
///     The member-level answer: walks references backwards from every changed declaration until the walk reaches test
///     methods. It refines the <see cref="TypeWalk"/>'s answer, which selects whole test classes, to the test methods
///     that can reach a change, and it stops past 300 symbols or 8 seconds.
/// </summary>
/// <remarks>
///     <para>
///         The walk follows callers, overridden members and the interface members a changed member implements (so
///         calls through an interface or a DI registration are followed). A reference inside a test method selects that
///         test; a reference elsewhere in a test class selects the whole class and every class derived from it, and the walk
///         goes on to that member's callers, so a test that reaches the change through a helper class is selected too.
///     </para>
///     <para>
///         A member with no caller in source goes to <see cref="HostRule"/>. Code an application host calls selects every
///         test project that depends on the application; a library member a framework calls through an external interface
///         or base class is reached through its type.
///     </para>
///     <para>One walk serves one request, over the solution of that request.</para>
/// </remarks>
internal sealed class MemberWalk
{
    // Each symbol costs one reference search, which takes up to a second on a large solution. Past either limit the
    // walk stops and the selector answers from the type walk, which takes milliseconds.
    private const int MaxSymbols = 300;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);

    private readonly RepoWorkspace _workspace;
    private readonly Solution _solution;
    private readonly ImmutableHashSet<Project> _reachedProjects;
    private readonly ImmutableHashSet<Document> _reachedDocuments;
    private readonly Dictionary<string, ProjectId> _byAssembly;
    private readonly TimeProvider _time;
    private readonly HashSet<ISymbol> _visited = new(SymbolEqualityComparer.Default);
    private readonly Queue<ISymbol> _queue = new();
    private readonly SelectionBuilder _selections = new();

    /// <param name="workspace">Gives the HEAD text of a changed file and the project graph.</param>
    /// <param name="solution">The current solution, with the reached projects loaded.</param>
    /// <param name="reachedProjects">The Roslyn projects of the changed projects and their dependents; references outside it are not searched.</param>
    /// <param name="time">Measures the walk against its 8 second budget.</param>
    public MemberWalk(RepoWorkspace workspace, Solution solution, IReadOnlyList<Project> reachedProjects, TimeProvider time)
    {
        _workspace = workspace;
        _solution = solution;
        _reachedProjects = [.. reachedProjects];
        _reachedDocuments = [.. reachedProjects.SelectMany(p => p.Documents)];
        _byAssembly = reachedProjects.GroupBy(p => p.AssemblyName).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        _time = time;
    }

    /// <summary>The selections found so far, keyed by test project file. It grows while the walk runs.</summary>
    public IReadOnlyDictionary<RepoPath, TestSelection> Selections => _selections.Selections;

    /// <summary>
    ///     Seeds the walk with the declarations that changed in <paramref name="path"/> and returns them, for the type
    ///     walk. A changed declaration in a test project is selected at once; top-level statements select every test
    ///     project behind the application whole.
    /// </summary>
    public async Task<List<(Document Document, SyntaxNode Node)>> SeedAsync(RepoPath path, CancellationToken cancellationToken)
    {
        var seeds = new List<(Document, SyntaxNode)>();
        var headText = _workspace.HeadText(path);
        foreach (var id in _solution.GetDocumentIdsWithFilePath(path.Absolute))
        {
            var document = _solution.GetDocument(id);
            if (document is null)
                continue;
            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (model is null || root is null)
                continue;
            var before = headText is null
                ? null
                : await CSharpSyntaxTree.ParseText(headText, (CSharpParseOptions)root.SyntaxTree.Options, cancellationToken: cancellationToken).GetRootAsync(cancellationToken).ConfigureAwait(false);
            var node = NodeOf(document.Project);
            foreach (var changed in CodeDiff.Find(before, root))
            {
                seeds.Add((document, changed));
                if (changed is CompilationUnitSyntax)
                {
                    // Top-level statements are the application's entry point.
                    if (node is not null)
                        SelectDependentsWhole(node, "top-level statements changed");
                    continue;
                }

                foreach (var symbol in Declared(model, changed, cancellationToken))
                {
                    if (node is { IsTest: true })
                        await SelectInTestProjectAsync(node, symbol, cancellationToken).ConfigureAwait(false);
                    else
                        Enqueue(symbol);
                }
            }
        }

        return seeds;
    }

    /// <summary>Runs the walk. Returns false when it went past 300 symbols or 8 seconds before finishing.</summary>
    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        while (_queue.Count > 0)
        {
            if (_visited.Count > MaxSymbols || _time.GetElapsedTime(started) > Budget)
                return false;

            var symbol = _queue.Dequeue();
            var referenced = false;
            foreach (var related in Related(symbol))
            {
                var references = await SymbolFinder.FindReferencesAsync(related, _solution, _reachedDocuments, cancellationToken).ConfigureAwait(false);
                foreach (var location in references.SelectMany(r => r.Locations))
                {
                    if (location.IsImplicit)
                        continue;
                    referenced = true;
                    await VisitReferenceAsync(location, cancellationToken).ConfigureAwait(false);
                }
            }

            if (referenced)
                continue;
            var project = _solution.GetProject(symbol.ContainingAssembly is null ? null : FindProjectId(symbol));
            var node = project is null ? null : NodeOf(project);
            if (node is null)
                continue;
            if (HostRule.IsCalledByHost(symbol, node))
                SelectDependentsWhole(node, $"{symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)} is called by an application host or a framework");
            else if (HostRule.IsFrameworkInvoked(symbol) && symbol.ContainingType is { } containing)
            {
                // A library member called through an external interface or base class (Equals, CompareTo,
                // ToString): tests reach it through its type.
                Enqueue(containing);
            }
        }

        return true;
    }

    /// <summary>
    ///     Selects every test project that depends on <paramref name="project"/> whole, and the project itself when it is a
    ///     test project. A project already selected whole keeps the first reason.
    /// </summary>
    public void SelectDependentsWhole(ProjectNode project, string reason)
    {
        foreach (var test in HostRule.DependentTestProjects(_workspace.Graph, project))
            SelectWhole(test, reason);
    }

    private ProjectNode? NodeOf(Project project) => project.FilePath is null ? null : _workspace.Graph.Find(_workspace.Root.PathOf(project.FilePath));

    private ProjectId? FindProjectId(ISymbol symbol) =>
        symbol.ContainingAssembly is { } assembly ? _byAssembly.GetValueOrDefault(assembly.Name) : null;

    private async Task VisitReferenceAsync(ReferenceLocation location, CancellationToken cancellationToken)
    {
        var document = location.Document;
        var node = NodeOf(document.Project);
        if (node is null)
            return;
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (model is null)
            return;
        var enclosing = Enclosing(model, location.Location.SourceSpan.Start, cancellationToken);
        if (enclosing is null)
            return;
        if (node.IsTest)
        {
            await SelectInTestProjectAsync(node, enclosing, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (enclosing is IMethodSymbol method && HostRule.IsEntryPoint(method))
        {
            SelectDependentsWhole(node, $"{node.Name}'s entry point calls the changed code");
            return;
        }

        Enqueue(enclosing);
    }

    private void Enqueue(ISymbol symbol)
    {
        if (_visited.Add(symbol))
            _queue.Enqueue(symbol);
    }

    private async Task SelectInTestProjectAsync(ProjectNode node, ISymbol symbol, CancellationToken cancellationToken)
    {
        if (_selections.IsWhole(node))
            return;
        if (symbol is IMethodSymbol method && TestAttributes.IsTest(method))
        {
            _selections.Add(node, TestName(method.ContainingType) + "." + method.Name);
            return;
        }

        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        if (type is null)
        {
            SelectWhole(node, "a test project changed outside any class");
            return;
        }

        // A helper, fixture or base class: every test in the class and in classes that derive from it, and the tests that
        // call it from other classes, which the walk reaches through its callers. A helper class holds no tests itself.
        Enqueue(symbol);
        if (!_selections.Add(node, TestName(type) + "."))
            return;
        if (type.TypeKind != TypeKind.Class)
            return;
        foreach (var derived in await SymbolFinder.FindDerivedClassesAsync(type, _solution, transitive: true, _reachedProjects, cancellationToken).ConfigureAwait(false))
        {
            var project = _solution.GetProject(FindProjectId(derived));
            if (project is not null && NodeOf(project) is { IsTest: true } testNode)
                _selections.Add(testNode, TestName(derived) + ".");
        }
    }

    private void SelectWhole(ProjectNode node, string reason)
    {
        if (!_selections.IsWhole(node))
            _selections.SelectWhole(node, reason);
    }

    /// <summary>The member or type whose code contains <paramref name="position"/>, looking through lambdas and local functions.</summary>
    private static ISymbol? Enclosing(SemanticModel model, int position, CancellationToken cancellationToken)
    {
        var symbol = model.GetEnclosingSymbol(position, cancellationToken);
        while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
            symbol = symbol.ContainingSymbol;
        if (symbol is INamespaceSymbol or null)
        {
            var token = model.SyntaxTree.GetRoot(cancellationToken).FindToken(position);
            var type = token.Parent?.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
            return type is null ? null : model.GetDeclaredSymbol(type, cancellationToken);
        }

        return symbol;
    }

    private static IEnumerable<ISymbol> Declared(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken)
    {
        switch (node)
        {
            case BaseFieldDeclarationSyntax field:
                foreach (var variable in field.Declaration.Variables)
                {
                    if (model.GetDeclaredSymbol(variable, cancellationToken) is { } symbol)
                        yield return symbol;
                }

                break;
            default:
                if (model.GetDeclaredSymbol(node, cancellationToken) is { } declared)
                    yield return declared;
                break;
        }
    }

    /// <summary>The symbol plus the members whose callers also reach it: overridden members and implemented interface members.</summary>
    private static IEnumerable<ISymbol> Related(ISymbol symbol)
    {
        yield return symbol;
        switch (symbol)
        {
            case IMethodSymbol { MethodKind: MethodKind.Constructor } constructor:
                yield return constructor.ContainingType;
                break;
            case IMethodSymbol method:
                for (var o = method.OverriddenMethod; o is not null; o = o.OverriddenMethod)
                    yield return o;
                break;
            case IPropertySymbol property:
                for (var o = property.OverriddenProperty; o is not null; o = o.OverriddenProperty)
                    yield return o;
                break;
        }

        if (symbol.ContainingType is { } type && symbol is IMethodSymbol or IPropertySymbol or IEventSymbol)
        {
            foreach (var member in type.AllInterfaces.SelectMany(i => i.GetMembers()))
            {
                if (SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), symbol))
                    yield return member;
            }
        }
    }

    /// <summary>The name test adapters report for a type: namespace, then nested types joined with <c>+</c>.</summary>
    private static string TestName(INamedTypeSymbol type)
    {
        var nesting = new Stack<string>();
        for (var t = type; t is not null; t = t.ContainingType)
            nesting.Push(t.MetadataName.Split('`')[0]);
        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() + "." : "";
        return ns + string.Join("+", nesting);
    }
}
