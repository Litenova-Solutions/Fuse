using System.Collections.Immutable;
using Fuse.Check.Model;
using Fuse.Graph;
using Fuse.Paths;
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
///             so the reach is <see cref="Reach.Broad"/> and the caller re-checks every file in the reached projects.
///         </item>
///     </list>
/// </remarks>
internal sealed class ChangeReach
{
    private readonly RepoWorkspace _workspace;
    private readonly Dictionary<string, HashSet<string>> _cache = new(StringComparer.Ordinal);
    private int _cachedGeneration = -1;

    public ChangeReach(RepoWorkspace workspace) => _workspace = workspace;

    /// <summary>True when the file has a declaration change against HEAD. Syntax only; binds nothing.</summary>
    public async Task<bool> HasDeclarationChangeAsync(string path, CancellationToken cancellationToken)
    {
        var head = _workspace.HeadText(path);
        var now = await CurrentTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (head is not null && now is not null && head.ContentEquals(now))
            return false;
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return true;
        var before = head is null ? [] : SurfaceMap.Compute(await ParseAsync(head, cancellationToken).ConfigureAwait(false));
        var after = now is null ? [] : SurfaceMap.Compute(await ParseAsync(now, cancellationToken).ConfigureAwait(false));
        // An added using changes nothing another file can see, so it alone does not send the check to the dependents.
        return SurfaceMap.Changes(before, after).Any(c => !(c.Key.StartsWith("U:", StringComparison.Ordinal) && c.Before is null));
    }

    /// <summary>
    ///     The files in the reached projects that the declaration changes in <paramref name="paths"/> can break. The reach
    ///     is <see cref="Reach.Precise"/>, with the change that made each file a candidate so an error there can name its
    ///     cause, unless one change is broad, which makes it <see cref="Reach.Broad"/>.
    /// </summary>
    /// <param name="paths">Absolute paths of the targets with a declaration change.</param>
    /// <param name="reached">The projects that own those targets, and their dependents, all loaded.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    public async Task<Reach> ReachAsync(IReadOnlyList<string> paths, IReadOnlyList<ProjectNode> reached, CancellationToken cancellationToken)
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
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                // A Razor component or view is used by its file name.
                var name = Path.GetFileNameWithoutExtension(path);
                names.Add((name, new Cause.Changed(name)));
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
                Cause cause = change.After is null ? new Cause.Removed(entry.Declaration) : new Cause.Changed(entry.Declaration);
                if (change.Key.StartsWith("U:", StringComparison.Ordinal))
                {
                    // An added using cannot change what another file sees. A removed one can change every signature in
                    // this file, so every file that names one of this file's types is a candidate.
                    if (cause is Cause.Removed)
                    {
                        foreach (var typeName in entry.Names)
                            names.Add((typeName, cause));
                    }

                    continue;
                }

                if (change.Key.StartsWith("G:", StringComparison.Ordinal) || change.Key.StartsWith("A:", StringComparison.Ordinal) || entry.Node is DelegateDeclarationSyntax)
                    return Broad(projects);
                if (change.Key.StartsWith("T:", StringComparison.Ordinal))
                {
                    if (change.Before is not null && change.After is not null)
                        return Broad(projects);
                    if (change.Before is not null && Declared(model, change.Before.Node, cancellationToken) is { } removedType)
                        changed.Add(new(removedType, cause, true));
                    else
                        names.Add((entry.Names[0], new Cause.Changed(entry.Declaration)));
                    continue;
                }

                if (change.Before is not null)
                {
                    if (Declared(model, change.Before.Node, cancellationToken) is not { } member)
                        return Broad(projects);
                    changed.Add(new(member, cause, true));
                    if (member is IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType: { } constructed })
                        derivedFrom.Add((constructed, cause));
                    continue;
                }

                // An added member of a type that existed at HEAD.
                if (entry.Container is null || !before.TryGetValue(entry.Container, out var container)
                    || Declared(model, container.Node, cancellationToken) is not INamedTypeSymbol containingType)
                    continue;
                var name = entry.Names[0];
                for (var type = containingType; type is not null; type = type.BaseType)
                    foreach (var member in type.GetMembers(name).Where(m => !m.IsImplicitlyDeclared))
                        changed.Add(new(member, cause, false));
                if (containingType.TypeKind == TypeKind.Interface || containingType.IsAbstract)
                    derivedFrom.Add((containingType, cause));
            }
        }

        // A file several changes reach keeps the first change that reached it, in the order the changes were seen.
        var causes = new Dictionary<string, Cause>(PathRules.PathComparer);
        foreach (var change in changed.GroupBy(c => c.Symbol, SymbolEqualityComparer.Default).Select(g => g.First()))
        {
            // A copy: the reference set is cached, and the implementations and overrides below are added to this one.
            var files = new HashSet<string>(await ReferencingFilesAsync(change.Symbol, reachKey, baseline, baselineDocuments, cancellationToken).ConfigureAwait(false), PathRules.PathComparer);
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
                    causes.TryAdd(document.FilePath, name.Cause);
            }
        }

        return new Reach.Precise(causes);
    }

    /// <summary>Every source file of <paramref name="projects"/>, for a change that a reference search cannot bound.</summary>
    private static Reach.Broad Broad(IReadOnlyList<Project> projects) =>
        new(projects.SelectMany(p => p.Documents).Select(d => d.FilePath).OfType<string>().ToHashSet(PathRules.PathComparer));

    private async Task<IEnumerable<string>> ReferencingFilesAsync(ISymbol symbol, string reachKey, Solution baseline, IImmutableSet<Document> documents, CancellationToken cancellationToken)
    {
        var key = (symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)) + "|" + reachKey;
        if (_cache.TryGetValue(key, out var cached))
            return cached;
        var references = await SymbolFinder.FindReferencesAsync(symbol, baseline, documents, cancellationToken).ConfigureAwait(false);
        var files = new HashSet<string>(PathRules.PathComparer);
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
    ///     One changed declaration resolved to its symbol at HEAD, with the cause an error in a file that uses it names,
    ///     and whether anything implements or derives from it.
    /// </summary>
    /// <param name="Implemented">Whether to look for implementations and overrides of the symbol.</param>
    private sealed record Change(ISymbol Symbol, Cause Cause, bool Implemented);
}
