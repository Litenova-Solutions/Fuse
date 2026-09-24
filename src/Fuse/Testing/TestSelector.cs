using System.Collections.Immutable;
using Fuse.Graph;
using Fuse.Repo;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Fuse.Testing;

/// <summary>
///     Selects the tests that can observe the working-tree changes, by walking references backwards from every
///     changed declaration until the walk reaches test methods.
/// </summary>
/// <remarks>
///     <para>
///         The walk follows callers, overridden members and the interface members a changed member implements (so
///         calls through an interface or a DI registration are followed). A reference inside a test method selects that
///         test; a reference elsewhere in a test class selects the whole class.
///     </para>
///     <para>
///         Code a framework calls (controller actions, request handlers, hosted services, top-level statements) has no
///         caller in source. When the walk reaches such a member, every test project that depends on its project is
///         selected whole, because an integration test can reach it through HTTP, a mediator or the host. The same
///         fallback applies to Razor changes and when the walk grows past <see cref="MaxSymbols"/>. A missed failing
///         test is the one outcome selection must not produce; running extra tests only costs time.
///     </para>
/// </remarks>
internal sealed class TestSelector
{
    private const int MaxSymbols = 3000;

    private readonly RepoWorkspace _workspace;

    public TestSelector(RepoWorkspace workspace) => _workspace = workspace;

    /// <summary>Selects tests for <paramref name="changedFiles"/>; the result maps each test project path to its selection.</summary>
    public async Task<Dictionary<string, ProjectSelection>> SelectAsync(IReadOnlyList<string> changedFiles, CancellationToken cancellationToken)
    {
        var graph = _workspace.Graph;
        var result = new Dictionary<string, ProjectSelection>(ChangeTracker.PathComparer);
        var changedProjects = changedFiles.SelectMany(graph.OwnersOf).DistinctBy(p => p.Path).ToList();
        var cone = changedProjects.Concat(changedProjects.SelectMany(graph.DependentsOf)).DistinctBy(p => p.Path).ToList();
        if (!cone.Any(p => p.IsTest))
            return result;
        await _workspace.EnsureLoadedAsync(cone, cancellationToken).ConfigureAwait(false);

        var solution = _workspace.Current;
        var coneProjects = cone.SelectMany(n => RepoWorkspace.ProjectsFor(solution, n)).ToList();
        var coneDocuments = coneProjects.SelectMany(p => p.Documents).ToImmutableHashSet();
        var walk = new Walk(this, solution, coneProjects, coneDocuments, result);

        foreach (var path in changedFiles)
        {
            var owners = graph.OwnersOf(path);
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var owner in owners)
                    walk.SelectDependentsWhole(owner, $"{Path.GetFileName(path)} changed");
                continue;
            }

            await walk.SeedAsync(path, cancellationToken).ConfigureAwait(false);
        }

        await walk.RunAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private ProjectNode? NodeOf(Project project) => project.FilePath is null ? null : _workspace.Graph.Find(project.FilePath);

    private sealed class Walk(TestSelector owner, Solution solution, List<Project> cone, ImmutableHashSet<Document> coneDocuments, Dictionary<string, ProjectSelection> result)
    {
        private readonly HashSet<ISymbol> _visited = new(SymbolEqualityComparer.Default);
        private readonly Queue<ISymbol> _queue = new();
        private bool _exhausted;

        public async Task SeedAsync(string path, CancellationToken cancellationToken)
        {
            var headText = owner._workspace.HeadText(path);
            foreach (var id in solution.GetDocumentIdsWithFilePath(path))
            {
                var document = solution.GetDocument(id);
                if (document is null)
                    continue;
                var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                if (model is null || root is null)
                    continue;
                var before = headText is null
                    ? null
                    : await CSharpSyntaxTree.ParseText(headText, (CSharpParseOptions)root.SyntaxTree.Options, cancellationToken: cancellationToken).GetRootAsync(cancellationToken).ConfigureAwait(false);
                var node = owner.NodeOf(document.Project);
                foreach (var changed in ChangedDeclarations.Find(before, root))
                {
                    if (changed is CompilationUnitSyntax)
                    {
                        // Top-level statements are the host's entry point.
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
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (_queue.Count > 0)
            {
                if (_visited.Count > MaxSymbols)
                {
                    _exhausted = true;
                    break;
                }

                var symbol = _queue.Dequeue();
                var referenced = false;
                foreach (var related in Related(symbol))
                {
                    var references = await SymbolFinder.FindReferencesAsync(related, solution, coneDocuments, cancellationToken).ConfigureAwait(false);
                    foreach (var location in references.SelectMany(r => r.Locations))
                    {
                        if (location.IsImplicit)
                            continue;
                        referenced = true;
                        await VisitReferenceAsync(location, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (!referenced && IsFrameworkInvoked(symbol))
                {
                    var project = solution.GetProject(symbol.ContainingAssembly is null ? null : FindProjectId(symbol));
                    if (project is not null && owner.NodeOf(project) is { } node)
                        SelectDependentsWhole(node, $"{symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)} is invoked by a framework");
                }
            }

            if (_exhausted)
            {
                foreach (var project in cone)
                {
                    if (owner.NodeOf(project) is { IsTest: true } node)
                        SelectWhole(node, "the change reaches too much code to trace");
                }
            }
        }

        private ProjectId? FindProjectId(ISymbol symbol) =>
            cone.FirstOrDefault(p => p.AssemblyName == symbol.ContainingAssembly.Name)?.Id;

        private async Task VisitReferenceAsync(ReferenceLocation location, CancellationToken cancellationToken)
        {
            var document = location.Document;
            var node = owner.NodeOf(document.Project);
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

            if (enclosing is IMethodSymbol method && IsEntryPoint(method))
            {
                SelectDependentsWhole(node, "the change reaches the application's entry point");
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
            var selection = Selection(node);
            if (selection.All)
                return;
            if (symbol is IMethodSymbol method && TestAttributes.IsTest(method))
            {
                selection.Patterns.Add(TestName(method.ContainingType) + "." + method.Name);
                return;
            }

            var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
            if (type is null)
            {
                SelectWhole(node, "a test project changed outside any class");
                return;
            }

            // A helper, fixture or base class: every test in the class and in classes that derive from it.
            if (!selection.Patterns.Add(TestName(type) + "."))
                return;
            if (type.TypeKind != TypeKind.Class)
                return;
            foreach (var derived in await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cone.ToImmutableHashSet(), cancellationToken).ConfigureAwait(false))
            {
                var project = solution.GetProject(FindProjectId(derived));
                if (project is not null && owner.NodeOf(project) is { IsTest: true } testNode && !Selection(testNode).All)
                    Selection(testNode).Patterns.Add(TestName(derived) + ".");
            }
        }

        public void SelectDependentsWhole(ProjectNode node, string reason)
        {
            if (node.IsTest)
                SelectWhole(node, reason);
            foreach (var dependent in owner._workspace.Graph.DependentsOf(node).Where(d => d.IsTest))
                SelectWhole(dependent, reason);
        }

        private void SelectWhole(ProjectNode node, string reason)
        {
            var selection = Selection(node);
            if (selection.All)
                return;
            selection.All = true;
            selection.AllReason = reason;
            selection.Patterns.Clear();
        }

        private ProjectSelection Selection(ProjectNode node)
        {
            if (!result.TryGetValue(node.Path, out var selection))
                result[node.Path] = selection = new ProjectSelection();
            return selection;
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

        /// <summary>
        ///     True when a framework, not source code, is expected to call the symbol: it overrides or implements a member
        ///     declared outside the repository, its type derives from a non-trivial external base class, it carries
        ///     attributes, or it is an entry point.
        /// </summary>
        private static bool IsFrameworkInvoked(ISymbol symbol)
        {
            if (symbol is IMethodSymbol method && IsEntryPoint(method))
                return true;
            if (symbol.DeclaredAccessibility is Accessibility.Private)
                return false;
            if (symbol is IMethodSymbol { OverriddenMethod: { } overridden } && !overridden.Locations.Any(l => l.IsInSource))
                return true;
            if (symbol.ContainingType is { } type)
            {
                if (type.AllInterfaces.SelectMany(i => i.GetMembers()).Any(m => !m.Locations.Any(l => l.IsInSource) && SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(m), symbol)))
                    return true;
                for (var b = type.BaseType; b is not null; b = b.BaseType)
                {
                    if (b.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum)
                        break;
                    if (!b.Locations.Any(l => l.IsInSource))
                        return true;
                }
            }

            var attributed = symbol.GetAttributes().Concat(symbol.ContainingType?.GetAttributes() ?? []);
            return attributed.Any(a => a.AttributeClass is { } c && !IsInertAttribute(c));
        }

        /// <summary>A <c>Main</c> method, or the method the compiler synthesizes for top-level statements (<c>&lt;Main&gt;$</c>).</summary>
        private static bool IsEntryPoint(IMethodSymbol method) =>
            method.IsStatic && method.Name is "Main" or WellKnownMemberNames.TopLevelStatementsEntryPointMethodName;

        private static bool IsInertAttribute(INamedTypeSymbol attribute) =>
            attribute.ContainingNamespace?.ToDisplayString() is "System.Runtime.CompilerServices" or "System.Diagnostics" or "System.Diagnostics.CodeAnalysis"
            || attribute.Name is "ObsoleteAttribute" or "SerializableAttribute" or "FlagsAttribute";
    }

    /// <summary>The name test adapters report for a type: namespace, then nested types joined with <c>+</c>.</summary>
    internal static string TestName(INamedTypeSymbol type)
    {
        var nesting = new Stack<string>();
        for (var t = type; t is not null; t = t.ContainingType)
            nesting.Push(t.MetadataName.Split('`')[0]);
        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() + "." : "";
        return ns + string.Join("+", nesting);
    }
}
