using System.Collections.Immutable;
using Fuse.Changes;
using Fuse.Changes.Model;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Testing.Caching;
using Fuse.Testing.Model;
using Fuse.Workspace.Generations;
using Fuse.Workspace.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
namespace Fuse.Testing.Selection;

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
///     <para>One walk serves one selection, over the current solution of the generation that selection pinned.</para>
/// </remarks>
internal sealed class MemberWalk
{
    // Each symbol costs one reference search, which takes up to a second on a large solution. Past either limit the
    // walk stops and the selector answers from the type walk, which takes milliseconds.
    private const int MaxSymbols = 300;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);

    private readonly RepoRoot _root;
    private readonly RepoGraph _graph;
    private readonly Solution _baseline;
    private readonly Solution _solution;
    private readonly ImmutableHashSet<Project> _reachedProjects;
    private readonly ImmutableHashSet<Document> _reachedDocuments;
    private readonly Dictionary<string, ProjectId> _byAssembly;
    private readonly TimeProvider _time;
    private readonly DynamicTestMap? _dynamicMap;
    private readonly SyntaxTokenIndex? _tokenIndex;
    private readonly HashSet<ProjectId> _reachedProjectIds;
    private readonly HashSet<ISymbol> _visited = new(SymbolEqualityComparer.Default);
    private readonly HashSet<ISymbol> _newlyAddedSymbols = new(SymbolEqualityComparer.Default);
    private readonly HashSet<RepoPath> _dynamicCoveredApplications = [];
    private readonly HashSet<RepoPath> _unmappedHostApplications = [];
    private readonly Queue<ISymbol> _queue = new();
    private readonly SelectionBuilder _selections = new();

    /// <param name="root">Turns the paths Roslyn reports into repository paths.</param>
    /// <param name="generation">The generation the walk reads: its graph, and its baseline for the HEAD text of a changed file.</param>
    /// <param name="solution">The generation's current solution, with the reached projects loaded.</param>
    /// <param name="reachedProjects">The Roslyn projects of the changed projects and their dependents; references outside it are not searched.</param>
    /// <param name="time">Measures the walk against its 8 second budget.</param>
    public MemberWalk(RepoRoot root, Generation generation, Solution solution, IReadOnlyList<Project> reachedProjects, TimeProvider time)
        : this(root, generation, solution, reachedProjects, time, null, null)
    {
    }

    internal MemberWalk(RepoRoot root, Generation generation, Solution solution, IReadOnlyList<Project> reachedProjects, TimeProvider time, DynamicTestMap? dynamicMap)
        : this(root, generation, solution, reachedProjects, time, dynamicMap, null)
    {
    }

    internal MemberWalk(RepoRoot root, Generation generation, Solution solution, IReadOnlyList<Project> reachedProjects, TimeProvider time, DynamicTestMap? dynamicMap, SyntaxTokenIndex? tokenIndex)
    {
        _root = root;
        _graph = generation.Graph;
        _baseline = generation.Baseline;
        _solution = solution;
        _reachedProjects = [.. reachedProjects];
        _reachedDocuments = [.. reachedProjects.SelectMany(p => p.Documents)];
        _reachedProjectIds = reachedProjects.Select(p => p.Id).ToHashSet();
        _byAssembly = reachedProjects.GroupBy(p => p.AssemblyName).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        _time = time;
        _dynamicMap = dynamicMap;
        _tokenIndex = tokenIndex;
    }

    /// <summary>The selections found so far, keyed by test project file. It grows while the walk runs.</summary>
    public IReadOnlyDictionary<RepoPath, TestSelection> Selections => _selections.Selections;

    /// <summary>Applications whose changed host-called members were resolved via <see cref="DynamicTestMap"/>.</summary>
    public IReadOnlySet<RepoPath> DynamicCoveredApplications => _dynamicCoveredApplications;

    /// <summary>Applications where at least one changed host-called member had no dynamic mapping and fell back to static rules.</summary>
    public IReadOnlySet<RepoPath> UnmappedHostApplications => _unmappedHostApplications;

    /// <summary>How many changed declarations <see cref="SeedAsync"/> has seen across its calls.</summary>
    public int ChangedDeclarations { get; private set; }

    /// <summary>How many of them compiled to the same code as HEAD and seeded nothing.</summary>
    public int DroppedSameCode { get; private set; }

    /// <summary>
    ///     Seeds the walk with the declarations that changed in <paramref name="path"/> and returns them, for the type
    ///     walk. A changed declaration in a test project is selected at once; top-level statements select every test
    ///     project behind the application whole.
    /// </summary>
    public async Task<List<(Document Document, SyntaxNode Node)>> SeedAsync(RepoPath path, CancellationToken cancellationToken)
    {
        var seeds = new List<(Document, SyntaxNode)>();
        foreach (var id in _solution.GetDocumentIdsWithFilePath(path.Absolute))
        {
            var document = _solution.GetDocument(id);
            if (document is null)
                continue;
            // Parse syntax and diff before forcing SemanticModel binding so comment/whitespace edits cost zero binding.
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null)
                continue;
            var baselineDocument = _baseline.GetDocument(id);
            var headText = baselineDocument is null ? null : await baselineDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);

            var before = headText is null
                ? null
                : await CSharpSyntaxTree.ParseText(headText, (CSharpParseOptions)root.SyntaxTree.Options, cancellationToken: cancellationToken).GetRootAsync(cancellationToken).ConfigureAwait(false);
            var diffs = CodeDiff.Find(before, root);
            if (diffs.Count == 0)
                continue;

            var node = NodeOf(document.Project);
            var oldDeclarations = before is null ? FileDeclarations.Empty : FileDeclarations.Of(before);
            var currentDeclarations = FileDeclarations.Of(root);
            var newlyAddedKeys = ComputeNewlyAddedKeys(oldDeclarations, currentDeclarations);

            SemanticModel? model = null;
            if (diffs.Any(d => d is not CompilationUnitSyntax))
            {
                model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (model is null)
                    continue;
            }

            foreach (var changed in diffs)
            {
                ChangedDeclarations++;
                if (changed is not CompilationUnitSyntax
                    && model is not null
                    && await BodyEquivalence.SameAsHeadAsync(document, changed, model, _baseline, cancellationToken).ConfigureAwait(false))
                {
                    DroppedSameCode++;
                    continue;
                }

                seeds.Add((document, changed));
                if (changed is CompilationUnitSyntax)
                {
                    // Top-level statements are the application's entry point.
                    if (node is not null)
                    {
                        _unmappedHostApplications.Add(node.Path);
                        SelectDependentsWhole(node, "top-level statements changed");
                    }

                    continue;
                }

                if (model is null)
                    continue;
                var isNewlyAdded = before is null || IsNewlyAddedSyntax(changed, currentDeclarations, newlyAddedKeys);
                foreach (var symbol in Declared(model, changed, cancellationToken))
                {
                    if (isNewlyAdded)
                        _newlyAddedSymbols.Add(symbol);
                    if (node is { IsTest: true })
                        await SelectInTestProjectAsync(node, symbol, cancellationToken).ConfigureAwait(false);
                    else
                    {
                        // Recorded coverage only adds tests to the selection; the walk below still follows the
                        // symbol to its callers, so a test the static walk selects is never dropped.
                        HostRule.TryResolveDynamicSelections(_dynamicMap, _graph, _root, symbol, isNewlyAdded, _selections);
                        Enqueue(symbol);
                    }
                }
            }
        }

        return seeds;
    }

    /// <summary>Runs the walk, recording how many symbols it visited and how long it ran.</summary>
    public async Task<WalkOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        while (_queue.Count > 0)
        {
            if (_visited.Count > MaxSymbols || _time.GetElapsedTime(started) > Budget)
                return new WalkOutcome(false, _visited.Count, (long)_time.GetElapsedTime(started).TotalMilliseconds);

            var symbol = _queue.Dequeue();
            var isNewlyAdded = _newlyAddedSymbols.Contains(symbol);
            var project = _solution.GetProject(symbol.ContainingAssembly is null ? null : FindProjectId(symbol));
            var node = project is null ? null : NodeOf(project);
            var referenced = false;
            foreach (var related in Related(symbol))
            {
                var references = await SymbolFinder.FindReferencesAsync(related, _solution, CandidateDocuments(related), cancellationToken).ConfigureAwait(false);
                foreach (var location in references.SelectMany(r => r.Locations))
                {
                    if (location.IsImplicit)
                        continue;
                    referenced = true;
                    await VisitReferenceAsync(location, cancellationToken).ConfigureAwait(false);
                }
            }

            if (referenced)
            {
                if (node is not null && HostRule.IsCalledByHost(symbol, node))
                    HostRule.TryResolveDynamicSelections(_dynamicMap, _graph, _root, symbol, isNewlyAdded, _selections);
                continue;
            }

            if (node is null)
                continue;
            if (HostRule.IsCalledByHost(symbol, node))
            {
                // Recorded coverage only adds tests to the selection; the static whole-project selection below
                // stands, so a test the static walks selected is never dropped.
                if (HostRule.TryResolveDynamicSelections(_dynamicMap, _graph, _root, symbol, isNewlyAdded, _selections))
                    _dynamicCoveredApplications.Add(node.Path);
                else
                    _unmappedHostApplications.Add(node.Path);
                SelectDependentsWhole(node, $"{symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)} is called by an application host or a framework");
            }
            else if (HostRule.IsFrameworkInvoked(symbol))
            {
                // Recorded coverage only adds tests; the walk still follows the type, as without a mapping.
                HostRule.TryResolveDynamicSelections(_dynamicMap, _graph, _root, symbol, isNewlyAdded, _selections);
                if (symbol.ContainingType is { } containing)
                {
                    // A library member called through an external interface or base class (Equals, CompareTo,
                    // ToString): tests reach it through its type.
                    Enqueue(containing);
                }
            }
        }

        return new WalkOutcome(true, _visited.Count, (long)_time.GetElapsedTime(started).TotalMilliseconds);
    }

    private static HashSet<DeclarationKey> ComputeNewlyAddedKeys(FileDeclarations oldDeclarations, FileDeclarations currentDeclarations)
    {
        var added = new HashSet<DeclarationKey>();
        foreach (var decl in currentDeclarations.All)
        {
            if (oldDeclarations.Find(decl.Key) is null)
                added.Add(decl.Key);
        }

        return added;
    }

    private static bool IsNewlyAddedSyntax(SyntaxNode changed, FileDeclarations currentDeclarations, HashSet<DeclarationKey> newlyAddedKeys)
    {
        if (newlyAddedKeys.Count == 0)
            return false;
        foreach (var decl in currentDeclarations.All)
        {
            if (!newlyAddedKeys.Contains(decl.Key))
                continue;
            if (decl.Parts.Any(p => ReferenceEquals(p, changed) || p.Span == changed.Span))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Selects every test project that depends on <paramref name="project"/> whole, and the project itself when it is a
    ///     test project. A project already selected whole keeps the first reason.
    /// </summary>
    public void SelectDependentsWhole(ProjectNode project, string reason)
    {
        foreach (var test in HostRule.DependentTestProjects(_graph, project))
            SelectWhole(test, reason);
    }

    private ProjectNode? NodeOf(Project project) => project.FilePath is null ? null : _graph.Find(_root.PathOf(project.FilePath));

    private ProjectId? FindProjectId(ISymbol symbol) =>
        symbol.ContainingAssembly is { } assembly ? _byAssembly.GetValueOrDefault(assembly.Name) : null;

    /// <summary>
    ///     The reached documents a reference to <paramref name="symbol"/> can sit in, narrowed through the syntax token
    ///     index by the member's name. Without an index, when the symbol has no name to narrow by, or when the index
    ///     fails, every reached document is searched, as before.
    /// </summary>
    private ImmutableHashSet<Document> CandidateDocuments(ISymbol symbol)
    {
        try
        {
            if (_tokenIndex is null || !TryGetSymbolFilterTokens(symbol, out var identifiers, out _))
                return _reachedDocuments;
            var candidates = _tokenIndex.CandidateDocuments(_solution, identifiers, [], _reachedProjectIds);
            return WithUnindexedAndDeclaring(candidates, symbol);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return _reachedDocuments;
        }
    }

    /// <summary>
    ///     The reached projects a class derived from <paramref name="type"/> can sit in, narrowed through the syntax
    ///     token index. Without an index or when the index fails, every reached project is searched, as before.
    /// </summary>
    private ImmutableHashSet<Project> CandidateProjectsForDerived(INamedTypeSymbol type)
    {
        try
        {
            if (_tokenIndex is null || type.IsAnonymousType || type.IsTupleType || string.IsNullOrEmpty(type.Name))
                return _reachedProjects;
            var candidates = _tokenIndex.CandidateDocuments(_solution, [], [type.Name], _reachedProjectIds);
            var builder = ImmutableHashSet.CreateBuilder<Project>();
            foreach (var document in candidates)
                builder.Add(document.Project);
            foreach (var project in _reachedProjects)
            {
                if (project.Documents.Any(d => string.IsNullOrEmpty(d.FilePath)))
                    builder.Add(project);
            }

            return builder.ToImmutable();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return _reachedProjects;
        }
    }

    private ImmutableHashSet<Document> WithUnindexedAndDeclaring(
        ImmutableHashSet<Document> candidates,
        ISymbol symbol)
    {
        var builder = candidates.ToBuilder();
        foreach (var document in _reachedDocuments)
        {
            if (string.IsNullOrEmpty(document.FilePath))
                builder.Add(document);
        }

        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            if (_solution.GetDocument(syntaxRef.SyntaxTree) is { } declaring && _reachedProjectIds.Contains(declaring.Project.Id))
                builder.Add(declaring);
        }

        return builder.ToImmutable();
    }

    private static bool TryGetSymbolFilterTokens(
        ISymbol symbol,
        out IReadOnlyList<string> identifiers,
        out IReadOnlyList<string> baseTypeNames)
    {
        identifiers = [];
        baseTypeNames = [];

        if (symbol.IsImplicitlyDeclared)
            return false;

        switch (symbol)
        {
            case IMethodSymbol method:
                if (method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor
                    or MethodKind.UserDefinedOperator or MethodKind.Conversion
                    or MethodKind.BuiltinOperator or MethodKind.Destructor)
                    return false;

                if (method.AssociatedSymbol is IPropertySymbol { IsIndexer: true })
                    return false;

                var methodName = SimpleMemberName(method.AssociatedSymbol?.Name ?? method.Name);
                if (string.IsNullOrEmpty(methodName) || IsImplicitPatternMember(methodName))
                    return false;

                identifiers = [methodName];
                baseTypeNames = method.ContainingType is { } methodOwner ? [methodOwner.Name] : [];
                return true;

            case IPropertySymbol property:
                if (property.IsIndexer)
                    return false;
                var propName = SimpleMemberName(property.Name);
                if (string.IsNullOrEmpty(propName) || IsImplicitPatternMember(propName))
                    return false;
                identifiers = [propName];
                baseTypeNames = property.ContainingType is { } propOwner ? [propOwner.Name] : [];
                return true;

            case IEventSymbol evt:
                var evtName = SimpleMemberName(evt.Name);
                if (string.IsNullOrEmpty(evtName))
                    return false;
                identifiers = [evtName];
                baseTypeNames = evt.ContainingType is { } evtOwner ? [evtOwner.Name] : [];
                return true;

            case INamedTypeSymbol type:
                if (type.IsAnonymousType || type.IsTupleType || string.IsNullOrEmpty(type.Name))
                    return false;
                identifiers = type.Name.Length > "Attribute".Length && type.Name.EndsWith("Attribute", StringComparison.Ordinal)
                    ? [type.Name, type.Name[..^"Attribute".Length]]
                    : [type.Name];
                baseTypeNames = [type.Name];
                return true;

            default:
                if (string.IsNullOrEmpty(symbol.Name))
                    return false;
                identifiers = [symbol.Name];
                return true;
        }
    }

    private static string SimpleMemberName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    private static bool IsImplicitPatternMember(string name) =>
        name is "GetEnumerator" or "GetAsyncEnumerator" or "MoveNext" or "MoveNextAsync"
            or "Current" or "Dispose" or "DisposeAsync"
            or "GetAwaiter" or "IsCompleted" or "GetResult" or "OnCompleted" or "UnsafeOnCompleted"
            or "Deconstruct" or "Add" or "Slice" or "Length" or "Count"
            or "GetPinnableReference" or "PrintMembers" or "EqualityContract" or "<Clone>$";

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
        foreach (var derived in await SymbolFinder.FindDerivedClassesAsync(type, _solution, transitive: true, CandidateProjectsForDerived(type), cancellationToken).ConfigureAwait(false))
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
