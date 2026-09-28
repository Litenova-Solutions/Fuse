using System.Collections.Immutable;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
///             so it is reported as broad and the caller re-checks every file in reach.
///         </item>
///     </list>
/// </remarks>
internal sealed class ChangeReach
{
    private readonly RepoWorkspace _workspace;
    private readonly Dictionary<string, HashSet<string>> _cache = new(StringComparer.Ordinal);
    private int _cachedGeneration = -1;

    public ChangeReach(RepoWorkspace workspace) => _workspace = workspace;

    /// <summary>True when the file's declarations differ from HEAD. Syntax only; binds nothing.</summary>
    public async Task<bool> HasSurfaceChangeAsync(string path, CancellationToken cancellationToken)
    {
        var head = _workspace.HeadText(path);
        var now = await CurrentTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (head is not null && now is not null && head.ContentEquals(now))
            return false;
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return true;
        var before = head is null ? [] : SurfaceMap.Compute(await ParseAsync(head, cancellationToken).ConfigureAwait(false));
        var after = now is null ? [] : SurfaceMap.Compute(await ParseAsync(now, cancellationToken).ConfigureAwait(false));
        return SurfaceMap.Changes(before, after).Count > 0;
    }

    /// <summary>The files in <paramref name="reach"/> that the declaration changes in <paramref name="paths"/> can break, or null when a change is broad.</summary>
    public async Task<HashSet<string>?> FilesAsync(IReadOnlyList<string> paths, IReadOnlyList<Project> reach, CancellationToken cancellationToken) =>
        (await ProvenanceAsync(paths, reach, cancellationToken).ConfigureAwait(false))?.Files;

    /// <summary>
    ///     The files the declaration changes in <paramref name="paths"/> can break, each with the changed declaration that
    ///     makes it a candidate, or null when a change is broad and the caller has to bind everything. The candidate set is
    ///     the same one <see cref="FilesAsync"/> returns; this keeps the reason for each file with it, so an error in a
    ///     candidate can name the declaration that put the file in scope.
    /// </summary>
    public async Task<ReachProvenance?> ProvenanceAsync(IReadOnlyList<string> paths, IReadOnlyList<Project> reach, CancellationToken cancellationToken)
    {
        if (_cachedGeneration != _workspace.BaselineGeneration)
        {
            _cache.Clear();
            _cachedGeneration = _workspace.BaselineGeneration;
        }

        var baseline = _workspace.Baseline;
        var reachIds = reach.Select(p => p.Id).ToHashSet();
        // Reference sets depend on which projects were searched; a background load widens the reach.
        var reachKey = string.Join(",", reachIds.Select(id => id.Id.ToString("N")).Order(StringComparer.Ordinal));
        var baselineProjects = baseline.Projects.Where(p => reachIds.Contains(p.Id)).ToImmutableHashSet();
        var baselineDocuments = baselineProjects.SelectMany(p => p.Documents).ToImmutableHashSet();

        var changed = new List<Change>();
        var derivedFrom = new List<(INamedTypeSymbol Type, string Declaration, bool Removed)>();
        var names = new List<(string Name, string Declaration)>();
        foreach (var path in paths)
        {
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                // A Razor component or view is used by its file name.
                var name = Path.GetFileNameWithoutExtension(path);
                names.Add((name, name));
                continue;
            }

            var baselineDocument = baseline.GetDocumentIdsWithFilePath(path).Select(baseline.GetDocument).FirstOrDefault(d => d is not null);
            var beforeRoot = baselineDocument is null ? null : await baselineDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var model = baselineDocument is null ? null : await baselineDocument.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var now = await CurrentTextAsync(path, cancellationToken).ConfigureAwait(false);
            var before = beforeRoot is null ? [] : SurfaceMap.Compute(beforeRoot);
            var after = now is null ? [] : SurfaceMap.Compute(await ParseAsync(now, cancellationToken).ConfigureAwait(false));

            foreach (var change in SurfaceMap.Changes(before, after))
            {
                var entry = change.After ?? change.Before!;
                // A removal has no working-tree declaration, so the one that goes with it is the one at HEAD.
                var removed = change.After is null;
                if (change.Key.StartsWith("G:", StringComparison.Ordinal) || change.Key.StartsWith("A:", StringComparison.Ordinal) || entry.Node is DelegateDeclarationSyntax)
                    return null;
                if (change.Key.StartsWith("T:", StringComparison.Ordinal))
                {
                    if (change.Before is not null && change.After is not null)
                        return null;
                    if (change.Before is not null && Declared(model, change.Before.Node, cancellationToken) is { } removedType)
                        changed.Add(new(removedType, entry.Declaration, removed, true));
                    else
                        names.Add((entry.Names[0], entry.Declaration));
                    continue;
                }

                if (change.Before is not null)
                {
                    if (Declared(model, change.Before.Node, cancellationToken) is not { } member)
                        return null;
                    changed.Add(new(member, entry.Declaration, removed, true));
                    if (member is IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType: { } constructed })
                        derivedFrom.Add((constructed, entry.Declaration, removed));
                    continue;
                }

                // An added member of a type that existed at HEAD.
                if (entry.Container is null || !before.TryGetValue(entry.Container, out var container)
                    || Declared(model, container.Node, cancellationToken) is not INamedTypeSymbol containingType)
                    continue;
                var name = entry.Names[0];
                for (var type = containingType; type is not null; type = type.BaseType)
                    foreach (var member in type.GetMembers(name).Where(m => !m.IsImplicitlyDeclared))
                        changed.Add(new(member, entry.Declaration, removed, false));
                if (containingType.TypeKind == TypeKind.Interface || containingType.IsAbstract)
                    derivedFrom.Add((containingType, entry.Declaration, removed));
            }
        }

        var provenance = new ReachProvenance();
        foreach (var change in changed.GroupBy(c => c.Symbol, SymbolEqualityComparer.Default).Select(g => g.First()))
        {
            // A copy: the reference set is cached, and the implementations and overrides below are added to this one.
            var files = new HashSet<string>(await ReferencingFilesAsync(change.Symbol, reachKey, baseline, baselineDocuments, cancellationToken).ConfigureAwait(false), Fuse.Repo.ChangeTracker.PathComparer);
            if (change.Implemented)
            {
                files.UnionWith(Declarations(await SymbolFinder.FindImplementationsAsync(change.Symbol, baseline, baselineProjects, cancellationToken).ConfigureAwait(false)));
                files.UnionWith(Declarations(await SymbolFinder.FindOverridesAsync(change.Symbol, baseline, baselineProjects, cancellationToken).ConfigureAwait(false)));
            }

            provenance.AddRange(files, change.Declaration, change.Removed);
        }

        foreach (var (type, declaration, removed) in derivedFrom.DistinctBy(d => d.Type, SymbolEqualityComparer.Default))
        {
            var files = Declarations(await SymbolFinder.FindDerivedClassesAsync(type, baseline, transitive: true, baselineProjects, cancellationToken).ConfigureAwait(false));
            if (type.TypeKind == TypeKind.Interface)
                files = files.Concat(Declarations(await SymbolFinder.FindImplementationsAsync(type, baseline, baselineProjects, cancellationToken).ConfigureAwait(false)));
            provenance.AddRange(files, declaration, removed);
        }

        if (names.Count > 0)
        {
            foreach (var document in reach.SelectMany(p => p.Documents.Concat<TextDocument>(p.AdditionalDocuments)))
            {
                if (document.FilePath is null)
                    continue;
                var text = (await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                var name = names.FirstOrDefault(n => text.Contains(n.Name, StringComparison.Ordinal));
                if (name.Name is not null)
                    provenance.Add(document.FilePath, name.Declaration, false);
            }
        }

        return provenance;
    }

    private async Task<IEnumerable<string>> ReferencingFilesAsync(ISymbol symbol, string reachKey, Solution baseline, IImmutableSet<Document> documents, CancellationToken cancellationToken)
    {
        var key = (symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)) + "|" + reachKey;
        if (_cache.TryGetValue(key, out var cached))
            return cached;
        var references = await SymbolFinder.FindReferencesAsync(symbol, baseline, documents, cancellationToken).ConfigureAwait(false);
        var files = new HashSet<string>(Fuse.Repo.ChangeTracker.PathComparer);
        foreach (var location in references.SelectMany(r => r.Locations))
        {
            if (location.Document.FilePath is { } file)
                files.Add(file);
        }

        _cache[key] = files;
        return files;
    }

    private static IEnumerable<string> Declarations(IEnumerable<ISymbol> symbols) =>
        symbols.SelectMany(s => s.Locations).Where(l => l.IsInSource).Select(l => l.SourceTree!.FilePath).Where(p => !string.IsNullOrEmpty(p));

    private static ISymbol? Declared(SemanticModel? model, SyntaxNode? node, CancellationToken cancellationToken) =>
        model is null || node is null ? null : model.GetDeclaredSymbol(node, cancellationToken);

    private async Task<SourceText?> CurrentTextAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;
        var current = _workspace.Current;
        var id = current.GetDocumentIdsWithFilePath(path).FirstOrDefault();
        TextDocument? document = id is null ? null : current.GetDocument(id) ?? (TextDocument?)current.GetAdditionalDocument(id);
        return document is not null
            ? await document.GetTextAsync(cancellationToken).ConfigureAwait(false)
            : RepoWorkspace.Decode(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
    }

    private static Task<SyntaxNode> ParseAsync(SourceText text, CancellationToken cancellationToken) =>
        CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), cancellationToken: cancellationToken)
            .GetRootAsync(cancellationToken);

    /// <summary>
    ///     One changed declaration resolved to its symbol at HEAD, with the declaration text an error can name and
    ///     whether anything implements or derives from it.
    /// </summary>
    /// <param name="Implemented">Whether to look for implementations and overrides of the symbol.</param>
    private sealed record Change(ISymbol Symbol, string Declaration, bool Removed, bool Implemented);
}
