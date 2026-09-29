using System.Collections.Immutable;
using System.Diagnostics;
using Fuse.Changes;
using Fuse.Changes.Model;
using Fuse.Check.Model;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace Fuse.Check;

/// <summary>
///     Decides which other files a declaration change can break, by resolving each changed declaration to its symbol
///     at HEAD and asking Roslyn where that symbol is used. The baseline does not change between edits, so the
///     reference sets are cached until HEAD moves or projects load.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>A changed or removed member: files referencing it, plus implementations and overrides of it.</item>
///         <item>A changed or removed constructor: additionally every type deriving from its type (implicit base calls).</item>
///         <item>
///             An added member: files referencing same-named members of the type and its bases (a new overload can
///             make a call ambiguous), and, for an interface or abstract type, every implementation.
///         </item>
///         <item>A removed type: files referencing it. An added type: files mentioning its name (it can clash with a same-named type).</item>
///         <item>
///             A changed type header, delegate, global using or assembly attribute can break code that never names it,
///             so the reach is <see cref="Reach.Broad"/> and the caller re-checks every file in the reached projects.
///         </item>
///     </list>
/// </remarks>
internal sealed class ChangeReach
{
    private readonly RepoWorkspace _workspace;
    private readonly Dictionary<string, HashSet<RepoPath>> _cache = new(StringComparer.Ordinal);
    private int _cachedGeneration = -1;

    public ChangeReach(RepoWorkspace workspace) => _workspace = workspace;

    /// <summary>True when the file has a declaration change against HEAD. Syntax only; binds nothing.</summary>
    public async Task<bool> HasDeclarationChangeAsync(RepoPath path, CancellationToken cancellationToken)
    {
        var head = _workspace.HeadText(path);
        var now = await CurrentTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (head is not null && now is not null && head.ContentEquals(now))
            return false;
        if (!path.Absolute.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return true;
        var before = head is null ? FileDeclarations.Empty : FileDeclarations.Of(await ParseAsync(head, cancellationToken).ConfigureAwait(false));
        var after = now is null ? FileDeclarations.Empty : FileDeclarations.Of(await ParseAsync(now, cancellationToken).ConfigureAwait(false));
        // An added using changes nothing another file can see, so it alone does not send the check to the dependents.
        return SurfaceDiff.Compare(before, after).Changes.Any(c => !(c is DeclarationChange.Added && c.Key is DeclarationKey.Using));
    }

    /// <summary>
    ///     The files in the reached projects that the declaration changes in <paramref name="paths"/> can break. The reach
    ///     is <see cref="Reach.Precise"/>, with the change that made each file a candidate so an error there can name its
    ///     cause, unless one change is broad, which makes it <see cref="Reach.Broad"/>.
    /// </summary>
    /// <param name="paths">The targets with a declaration change.</param>
    /// <param name="reached">The projects that own those targets, and their dependents, all loaded.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    public async Task<Reach> ReachAsync(IReadOnlyList<RepoPath> paths, IReadOnlyList<ProjectNode> reached, CancellationToken cancellationToken)
    {
        var projects = reached.SelectMany(n => RepoWorkspace.ProjectsFor(_workspace.Current, n)).ToList();
        if (_cachedGeneration != _workspace.BaselineGeneration)
        {
            _cache.Clear();
            _cachedGeneration = _workspace.BaselineGeneration;
        }

        var baseline = _workspace.Baseline;
        var reachIds = projects.Select(p => p.Id).ToHashSet();
        // Reference sets depend on which projects were searched; a background load widens the reach.
        var reachKey = string.Join(",", reachIds.Select(id => id.Id.ToString("N")).Order(StringComparer.Ordinal));
        var baselineProjects = baseline.Projects.Where(p => reachIds.Contains(p.Id)).ToImmutableHashSet();
        var baselineDocuments = baselineProjects.SelectMany(p => p.Documents).ToImmutableHashSet();

        var changed = new List<Change>();
        var derivedFrom = new List<(INamedTypeSymbol Type, Cause Cause)>();
        var names = new List<(string Name, Cause Cause)>();
        foreach (var path in paths)
        {
            if (!path.Absolute.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                // A Razor component or view is used by its file name.
                var name = Path.GetFileNameWithoutExtension(path.Absolute);
                names.Add((name, new Cause.Changed(name)));
                continue;
            }

            var baselineDocument = baseline.GetDocumentIdsWithFilePath(path.Absolute).Select(baseline.GetDocument).FirstOrDefault(d => d is not null);
            var beforeRoot = baselineDocument is null ? null : await baselineDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var model = baselineDocument is null ? null : await baselineDocument.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var now = await CurrentTextAsync(path, cancellationToken).ConfigureAwait(false);
            var before = beforeRoot is null ? FileDeclarations.Empty : FileDeclarations.Of(beforeRoot);
            var after = now is null ? FileDeclarations.Empty : FileDeclarations.Of(await ParseAsync(now, cancellationToken).ConfigureAwait(false));
            var changes = SurfaceDiff.Compare(before, after);
            if (changes.HasBroadChange)
                return Broad(projects);

            foreach (var change in changes.Changes)
            {
                var cause = CauseOf(change);
                if (change.Key is DeclarationKey.Using)
                {
                    // An added using cannot change what another file sees. A removed one can change every signature in
                    // this file, so every file that names one of this file's types is a candidate.
                    if (cause is Cause.Removed)
                    {
                        foreach (var typeName in change.Names)
                            names.Add((typeName, cause));
                    }

                    continue;
                }

                if (change.Key is DeclarationKey.NamedType)
                {
                    // A type whose header changed made the reach broad above, so this type was added or removed.
                    if (change is DeclarationChange.Removed && Declared(model, before.Find(change.Key)?.Node, cancellationToken) is { } removedType)
                        changed.Add(new(removedType, cause, true));
                    else
                        names.Add((change.Names[0], new Cause.Changed(cause.Declaration)));
                    continue;
                }

                if (change is not DeclarationChange.Added)
                {
                    if (Declared(model, before.Find(change.Key)?.Node, cancellationToken) is not { } member)
                        return Broad(projects);
                    changed.Add(new(member, cause, true));
                    if (member is IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType: { } constructed })
                        derivedFrom.Add((constructed, cause));
                    continue;
                }

                // An added member of a type that existed at HEAD.
                if (change.Key is not DeclarationKey.Member { Container: var containerKey } || before.Find(containerKey) is not { } container
                    || Declared(model, container.Node, cancellationToken) is not INamedTypeSymbol containingType)
                    continue;
                var name = change.Names[0];
                for (var type = containingType; type is not null; type = type.BaseType)
                    foreach (var member in type.GetMembers(name).Where(m => !m.IsImplicitlyDeclared))
                        changed.Add(new(member, cause, false));
                if (containingType.TypeKind == TypeKind.Interface || containingType.IsAbstract)
                    derivedFrom.Add((containingType, cause));
            }
        }

        // A file several changes reach keeps the first change that reached it, in the order the changes were seen.
        var causes = new Dictionary<RepoPath, Cause>();
        foreach (var change in changed.GroupBy(c => c.Symbol, SymbolEqualityComparer.Default).Select(g => g.First()))
        {
            // A copy: the reference set is cached, and the implementations and overrides below are added to this one.
            var files = new HashSet<RepoPath>(await ReferencingFilesAsync(change.Symbol, reachKey, baseline, baselineDocuments, cancellationToken).ConfigureAwait(false));
            if (change.Implemented)
            {
                files.UnionWith(Declarations(await SymbolFinder.FindImplementationsAsync(change.Symbol, baseline, baselineProjects, cancellationToken).ConfigureAwait(false)));
                files.UnionWith(Declarations(await SymbolFinder.FindOverridesAsync(change.Symbol, baseline, baselineProjects, cancellationToken).ConfigureAwait(false)));
            }

            foreach (var file in files)
                causes.TryAdd(file, change.Cause);
        }

        foreach (var (type, cause) in derivedFrom.DistinctBy(d => d.Type, SymbolEqualityComparer.Default))
        {
            var files = Declarations(await SymbolFinder.FindDerivedClassesAsync(type, baseline, transitive: true, baselineProjects, cancellationToken).ConfigureAwait(false));
            if (type.TypeKind == TypeKind.Interface)
                files = files.Concat(Declarations(await SymbolFinder.FindImplementationsAsync(type, baseline, baselineProjects, cancellationToken).ConfigureAwait(false)));
            foreach (var file in files)
                causes.TryAdd(file, cause);
        }

        if (names.Count > 0)
        {
            foreach (var document in projects.SelectMany(p => p.Documents.Concat<TextDocument>(p.AdditionalDocuments)))
            {
                if (document.FilePath is null)
                    continue;
                var text = (await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                var name = names.FirstOrDefault(n => text.Contains(n.Name, StringComparison.Ordinal));
                if (name.Name is not null)
                    causes.TryAdd(_workspace.Root.PathOf(document.FilePath), name.Cause);
            }
        }

        return new Reach.Precise(causes);
    }

    /// <summary>
    ///     The cause an error in a file this change reaches names. A removal has no working-tree declaration, so the one
    ///     that goes with it is the one at HEAD.
    /// </summary>
    private static Cause CauseOf(DeclarationChange change) => change switch
    {
        DeclarationChange.Added added => new Cause.Changed(added.After),
        DeclarationChange.Changed edited => new Cause.Changed(edited.After),
        DeclarationChange.Removed removed => new Cause.Removed(removed.Before),
        _ => throw new UnreachableException($"a declaration change is added, changed or removed, not {change.GetType().Name}"),
    };

    /// <summary>Every source file of <paramref name="projects"/>, for a change that a reference search cannot bound.</summary>
    private Reach.Broad Broad(IReadOnlyList<Project> projects) =>
        new(projects.SelectMany(p => p.Documents).Select(d => d.FilePath).OfType<string>().Select(_workspace.Root.PathOf).ToHashSet());

    private async Task<IEnumerable<RepoPath>> ReferencingFilesAsync(ISymbol symbol, string reachKey, Solution baseline, IImmutableSet<Document> documents, CancellationToken cancellationToken)
    {
        var key = (symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)) + "|" + reachKey;
        if (_cache.TryGetValue(key, out var cached))
            return cached;
        var references = await SymbolFinder.FindReferencesAsync(symbol, baseline, documents, cancellationToken).ConfigureAwait(false);
        var files = new HashSet<RepoPath>();
        // A file holds many references; its path is made once.
        foreach (var document in references.SelectMany(r => r.Locations).Select(l => l.Document).DistinctBy(d => d.Id))
        {
            if (document.FilePath is { } file)
                files.Add(_workspace.Root.PathOf(file));
        }

        _cache[key] = files;
        return files;
    }

    private IEnumerable<RepoPath> Declarations(IEnumerable<ISymbol> symbols) =>
        symbols.SelectMany(s => s.Locations).Where(l => l.IsInSource).Select(l => l.SourceTree!.FilePath).Where(p => !string.IsNullOrEmpty(p)).Select(_workspace.Root.PathOf);

    private static ISymbol? Declared(SemanticModel? model, SyntaxNode? node, CancellationToken cancellationToken) =>
        model is null || node is null ? null : model.GetDeclaredSymbol(node, cancellationToken);

    private async Task<SourceText?> CurrentTextAsync(RepoPath path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path.Absolute))
            return null;
        var current = _workspace.Current;
        var id = current.GetDocumentIdsWithFilePath(path.Absolute).FirstOrDefault();
        TextDocument? document = id is null ? null : current.GetDocument(id) ?? (TextDocument?)current.GetAdditionalDocument(id);
        return document is not null
            ? await document.GetTextAsync(cancellationToken).ConfigureAwait(false)
            : RepoWorkspace.Decode(await File.ReadAllBytesAsync(path.Absolute, cancellationToken).ConfigureAwait(false));
    }

    private static Task<SyntaxNode> ParseAsync(SourceText text, CancellationToken cancellationToken) =>
        CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), cancellationToken: cancellationToken)
            .GetRootAsync(cancellationToken);

    /// <summary>
    ///     One changed declaration resolved to its symbol at HEAD, with the cause an error in a file that uses it names,
    ///     and whether anything implements or derives from it.
    /// </summary>
    /// <param name="Implemented">Whether to look for implementations and overrides of the symbol.</param>
    private sealed record Change(ISymbol Symbol, Cause Cause, bool Implemented);
}
