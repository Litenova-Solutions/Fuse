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
///     <para>
///         A file is compared once for each project that compiles it, which is once per target framework, with that
///         project's parse options, so a declaration inside <c>#if DEBUG</c> or <c>#if NET8_0_OR_GREATER</c> is compared
///         where its symbol is defined. A change any framework sees counts.
///     </para>
///     <list type="bullet">
///         <item>A changed or removed member: files referencing it, plus implementations and overrides of it.</item>
///         <item>A changed or removed constructor: additionally every type deriving from its type (implicit base calls).</item>
///         <item>
///             An added instance constructor: files using any constructor of its type, the implicit one included, which it
///             can remove or make ambiguous, and every type deriving from its type.
///         </item>
///         <item>
///             An added member: files referencing same-named members of the type and its bases (a new overload can
///             make a call ambiguous), and, for an interface or abstract type, every implementation.
///         </item>
///         <item>A removed type: files referencing it. An added type: files mentioning its name (it can clash with a same-named type).</item>
///         <item>
///             A conversion operator: additionally every file mentioning its type's name, because a reference search does
///             not return the places an implicit conversion is applied.
///         </item>
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

    /// <summary>
    ///     True when the file has a declaration change against HEAD in any target framework it compiles for. Syntax only;
    ///     binds nothing.
    /// </summary>
    public async Task<bool> HasDeclarationChangeAsync(RepoPath path, CancellationToken cancellationToken)
    {
        var head = _workspace.HeadText(path);
        var now = await CurrentTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (head is not null && now is not null && head.ContentEquals(now))
            return false;
        if (!path.Absolute.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var version in await VersionsAsync(path, cancellationToken).ConfigureAwait(false))
        {
            // An added using changes nothing another file can see, so it alone does not send the check to the dependents.
            if (SurfaceDiff.Compare(version.Before, version.After).Changes.Any(c => !(c is DeclarationChange.Added && c.Key is DeclarationKey.Using)))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     The files in the reached projects that the declaration changes in <paramref name="paths"/> can break. The reach
    ///     is <see cref="Reach.Precise"/>, with the change that made each file a candidate so an error there can name its
    ///     cause, unless one change is broad, which makes it <see cref="Reach.Broad"/>.
    /// </summary>
    /// <param name="paths">The targets with a declaration change, in any order: they are taken in path order.</param>
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

        // Every way the changes reach other files, in the order that decides which cause a file gets: the targets in
        // path order, and each target's changes in the order SurfaceDiff lists them.
        var routes = new List<Route>();
        // A change that several target frameworks see is followed once, from the first framework that has it.
        var seen = new HashSet<(DeclarationKey Key, Cause Cause)>();
        foreach (var path in paths.OrderBy(p => p.Absolute, StringComparer.Ordinal))
        {
            if (!path.Absolute.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                // A Razor component or view is used by its file name.
                var name = Path.GetFileNameWithoutExtension(path.Absolute);
                routes.Add(new Route.ByName(name, new Cause.Changed(name)));
                continue;
            }

            foreach (var version in await VersionsAsync(path, cancellationToken).ConfigureAwait(false))
            {
                var changes = SurfaceDiff.Compare(version.Before, version.After);
                if (changes.HasBroadChange)
                    return Broad(projects);
                foreach (var change in changes.Changes)
                {
                    var cause = CauseOf(change);
                    if (seen.Add((change.Key, cause)) && !await AddRoutesAsync(routes, change, cause, version, cancellationToken).ConfigureAwait(false))
                        return Broad(projects);
                }
            }
        }

        var causes = await CausesAsync(routes, projects, cancellationToken).ConfigureAwait(false);
        return new Reach.Precise(causes.ToDictionary(c => c.Key, c => c.Value[0]), causes);
    }

    /// <summary>
    ///     Adds the routes by which <paramref name="change"/> reaches other files. Returns false when the change cannot be
    ///     bounded, because HEAD's declaration does not resolve to a symbol.
    /// </summary>
    private static async Task<bool> AddRoutesAsync(List<Route> routes, DeclarationChange change, Cause cause, FileVersion version, CancellationToken cancellationToken)
    {
        if (change.Key is DeclarationKey.Using)
        {
            // An added using cannot change what another file sees. A removed one can change every signature in this file,
            // so every file that names one of this file's types is a candidate.
            if (cause is Cause.Removed)
                routes.AddRange(change.Names.Select(typeName => new Route.ByName(typeName, cause)));
            return true;
        }

        if ((version.After.Find(change.Key) ?? version.Before.Find(change.Key))?.Node is ConversionOperatorDeclarationSyntax)
        {
            // A reference search does not return the places an implicit conversion is applied, so every file that names the
            // type is a candidate too.
            routes.Add(new Route.ByName(change.Names[0], cause));
        }

        if (change.Key is DeclarationKey.NamedType)
        {
            // A type whose header changed made the reach broad, so this type was added or removed.
            if (change is DeclarationChange.Removed && await version.DeclaredAsync(version.Before.Find(change.Key)?.Node, cancellationToken).ConfigureAwait(false) is { } removedType)
                routes.Add(new Route.BySymbol(removedType, IncludesImplementations: true, cause));
            else
                routes.Add(new Route.ByName(change.Names[0], new Cause.Changed(cause.Declaration)));
            return true;
        }

        if (change is not DeclarationChange.Added)
        {
            if (await version.DeclaredAsync(version.Before.Find(change.Key)?.Node, cancellationToken).ConfigureAwait(false) is not { } member)
                return false;
            routes.Add(new Route.BySymbol(member, IncludesImplementations: true, cause));
            if (member is IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType: { } constructed })
                routes.Add(new Route.ByDerivedTypes(constructed, cause));
            return true;
        }

        // An added member of a type that existed at HEAD.
        if (change.Key is not DeclarationKey.Member { Container: var containerKey } || version.Before.Find(containerKey) is not { } container
            || await version.DeclaredAsync(container.Node, cancellationToken).ConfigureAwait(false) is not INamedTypeSymbol containingType)
            return true;
        if (version.After.Find(change.Key)?.Node is ConstructorDeclarationSyntax ctor && !ctor.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            // An added constructor can remove the implicit parameterless one or make a construction ambiguous, and every
            // derived constructor calls one of them without naming it.
            routes.AddRange(containingType.InstanceConstructors.Select(c => new Route.BySymbol(c, IncludesImplementations: false, cause)));
            routes.Add(new Route.ByDerivedTypes(containingType, cause));
            return true;
        }

        var name = change.Names[0];
        for (var type = containingType; type is not null; type = type.BaseType)
            routes.AddRange(type.GetMembers(name).Where(m => !m.IsImplicitlyDeclared).Select(m => new Route.BySymbol(m, IncludesImplementations: false, cause)));
        if (containingType.TypeKind == TypeKind.Interface || containingType.IsAbstract)
            routes.Add(new Route.ByDerivedTypes(containingType, cause));
        return true;
    }

    /// <summary>
    ///     Each file the routes reach, with the cause of every route that reaches it, in route order, so the causes do not
    ///     depend on the order of any set. A symbol or a type is searched once however many routes name it, and every name
    ///     is looked for in one pass over the reached projects' documents.
    /// </summary>
    private async Task<Dictionary<RepoPath, IReadOnlyList<Cause>>> CausesAsync(IReadOnlyList<Route> routes, IReadOnlyList<Project> projects, CancellationToken cancellationToken)
    {
        var baseline = _workspace.Baseline;
        var searchedIds = projects.Select(p => p.Id).ToHashSet();
        // Reference sets depend on which projects were searched; a background load adds projects to the search.
        var searchKey = string.Join(",", searchedIds.Select(id => id.Id.ToString("N")).Order(StringComparer.Ordinal));
        var baselineProjects = baseline.Projects.Where(p => searchedIds.Contains(p.Id)).ToImmutableHashSet();
        var baselineDocuments = baselineProjects.SelectMany(p => p.Documents).ToImmutableHashSet();

        // The indexes in routes of the routes that reach each file.
        var reachedBy = new Dictionary<RepoPath, SortedSet<int>>();
        void Add(RepoPath file, int index)
        {
            if (!reachedBy.TryGetValue(file, out var indexes))
                reachedBy[file] = indexes = [];
            indexes.Add(index);
        }

        var searched = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var searchedWithImplementations = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var searchedTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        for (var index = 0; index < routes.Count; index++)
        {
            // A route whose symbol or type an earlier route searched reaches no file first, so it is not searched again.
            var files = new List<RepoPath>();
            if (routes[index] is Route.BySymbol symbol && (symbol.IncludesImplementations ? searchedWithImplementations : searched).Add(symbol.Symbol))
            {
                searched.Add(symbol.Symbol);
                files.AddRange(await ReferencingFilesAsync(symbol.Symbol, searchKey, baseline, baselineDocuments, cancellationToken).ConfigureAwait(false));
                if (symbol.IncludesImplementations)
                {
                    files.AddRange(Declarations(await SymbolFinder.FindImplementationsAsync(symbol.Symbol, baseline, baselineProjects, cancellationToken).ConfigureAwait(false)));
                    files.AddRange(Declarations(await SymbolFinder.FindOverridesAsync(symbol.Symbol, baseline, baselineProjects, cancellationToken).ConfigureAwait(false)));
                }
            }
            else if (routes[index] is Route.ByDerivedTypes derived && searchedTypes.Add(derived.Type))
            {
                files.AddRange(Declarations(await SymbolFinder.FindDerivedClassesAsync(derived.Type, baseline, transitive: true, baselineProjects, cancellationToken).ConfigureAwait(false)));
                if (derived.Type.TypeKind == TypeKind.Interface)
                    files.AddRange(Declarations(await SymbolFinder.FindImplementationsAsync(derived.Type, baseline, baselineProjects, cancellationToken).ConfigureAwait(false)));
            }

            foreach (var file in files)
                Add(file, index);
        }

        var byName = routes.Select((route, index) => (Route: route as Route.ByName, Index: index)).Where(r => r.Route is not null).ToList();
        if (byName.Count > 0)
        {
            foreach (var document in projects.SelectMany(p => p.Documents.Concat<TextDocument>(p.AdditionalDocuments)))
            {
                if (document.FilePath is null)
                    continue;
                var text = (await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                RepoPath? file = null;
                foreach (var (route, index) in byName)
                {
                    if (!text.Contains(route!.Name, StringComparison.Ordinal))
                        continue;
                    file ??= _workspace.Root.PathOf(document.FilePath);
                    Add(file.Value, index);
                }
            }
        }

        return reachedBy.ToDictionary(f => f.Key, IReadOnlyList<Cause> (f) => [.. f.Value.Select(i => routes[i].Cause).Distinct()]);
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

    private async Task<IEnumerable<RepoPath>> ReferencingFilesAsync(ISymbol symbol, string searchKey, Solution baseline, IImmutableSet<Document> documents, CancellationToken cancellationToken)
    {
        var key = (symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)) + "|" + searchKey;
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

    /// <summary>
    ///     The file at HEAD and in the working tree, once for each project that compiles it: HEAD's from the baseline's
    ///     document and the working tree's from the current one, each parsed with that project's options. When no loaded
    ///     project compiles the file, both texts are parsed with no preprocessor symbols and no symbol resolves.
    /// </summary>
    private async Task<List<FileVersion>> VersionsAsync(RepoPath path, CancellationToken cancellationToken)
    {
        var baseline = _workspace.Baseline;
        var current = _workspace.Current;
        var versions = new List<FileVersion>();
        var projectIds = baseline.GetDocumentIdsWithFilePath(path.Absolute).Concat(current.GetDocumentIdsWithFilePath(path.Absolute)).Select(id => id.ProjectId).Distinct();
        foreach (var projectId in projectIds)
        {
            var before = DocumentIn(baseline, projectId, path);
            var after = DocumentIn(current, projectId, path);
            if (before is null && after is null)
                continue;
            versions.Add(new FileVersion(
                await DeclarationsOfAsync(before, cancellationToken).ConfigureAwait(false),
                await DeclarationsOfAsync(after, cancellationToken).ConfigureAwait(false),
                before));
        }

        if (versions.Count > 0)
            return versions;
        var head = _workspace.HeadText(path);
        var now = await CurrentTextAsync(path, cancellationToken).ConfigureAwait(false);
        return
        [
            new FileVersion(
                head is null ? FileDeclarations.Empty : FileDeclarations.Of(await ParseAsync(head, cancellationToken).ConfigureAwait(false)),
                now is null ? FileDeclarations.Empty : FileDeclarations.Of(await ParseAsync(now, cancellationToken).ConfigureAwait(false)),
                null),
        ];
    }

    private static Document? DocumentIn(Solution solution, ProjectId projectId, RepoPath path) =>
        solution.GetDocumentIdsWithFilePath(path.Absolute).Where(id => id.ProjectId == projectId).Select(solution.GetDocument).FirstOrDefault(d => d is not null);

    private static async Task<FileDeclarations> DeclarationsOfAsync(Document? document, CancellationToken cancellationToken) =>
        document is not null && await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is { } root ? FileDeclarations.Of(root) : FileDeclarations.Empty;

    private async Task<SourceText?> CurrentTextAsync(RepoPath path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path.Absolute))
            return null;
        var current = _workspace.Current;
        var id = current.GetDocumentIdsWithFilePath(path.Absolute).FirstOrDefault();
        TextDocument? document = id is null ? null : current.GetDocument(id) ?? (TextDocument?)current.GetAdditionalDocument(id);
        return document is not null
            ? await document.GetTextAsync(cancellationToken).ConfigureAwait(false)
            : SolutionViews.Decode(await File.ReadAllBytesAsync(path.Absolute, cancellationToken).ConfigureAwait(false));
    }

    private static Task<SyntaxNode> ParseAsync(SourceText text, CancellationToken cancellationToken) =>
        CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), cancellationToken: cancellationToken)
            .GetRootAsync(cancellationToken);

    /// <summary>
    ///     One project's reading of a target: its declarations at HEAD and in the working tree, and the baseline document
    ///     whose semantic model resolves HEAD's declarations to symbols. The model is bound on first use, because a target
    ///     framework whose changes another framework already showed needs none.
    /// </summary>
    private sealed class FileVersion
    {
        private readonly Document? _baseline;
        private SemanticModel? _model;

        public FileVersion(FileDeclarations before, FileDeclarations after, Document? baseline)
        {
            Before = before;
            After = after;
            _baseline = baseline;
        }

        /// <summary>The declarations at HEAD, read from the baseline document when there is one.</summary>
        public FileDeclarations Before { get; }

        /// <summary>The declarations in the working tree.</summary>
        public FileDeclarations After { get; }

        /// <summary>The symbol a node of <see cref="Before"/> declares, or null when HEAD's version has no baseline document.</summary>
        public async Task<ISymbol?> DeclaredAsync(SyntaxNode? node, CancellationToken cancellationToken)
        {
            if (_baseline is null || node is null)
                return null;
            _model ??= await _baseline.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            return _model?.GetDeclaredSymbol(node, cancellationToken);
        }
    }

    /// <summary>One way a declaration change reaches other files, with the cause an error in such a file names.</summary>
    private abstract record Route(Cause Cause)
    {
        /// <summary>The files that use <paramref name="Symbol"/>, and the files that implement or override it when asked.</summary>
        public sealed record BySymbol(ISymbol Symbol, bool IncludesImplementations, Cause Cause) : Route(Cause);

        /// <summary>The files that declare a type deriving from <paramref name="Type"/> or, for an interface, implementing it.</summary>
        public sealed record ByDerivedTypes(INamedTypeSymbol Type, Cause Cause) : Route(Cause);

        /// <summary>The files whose text holds <paramref name="Name"/>, for a change code can reach without naming a symbol Roslyn finds.</summary>
        public sealed record ByName(string Name, Cause Cause) : Route(Cause);
    }
}
