using System.Collections.Concurrent;
using System.Collections.Immutable;
using Fuse.Paths;
using Fuse.Workspace.Generations;
using Fuse.Workspace.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
namespace Fuse.Check.Candidates;

/// <summary>
///     Finds the files that use a symbol or mention its name across the baseline documents. Each search is narrowed
///     by the syntax token index, and the reference sets are kept in a concurrent cache keyed by the baseline
///     generation and the symbol, so repeated checks reuse them. The baseline does not change between edits.
/// </summary>
internal sealed class SymbolReferenceFinder
{
    /// <summary>The most reference sets kept; past it the set unused the longest goes.</summary>
    internal const int MaxCacheEntries = 128;

    private readonly RepoRoot _root;
    private readonly SyntaxTokenIndex _tokenIndex;
    private readonly ConcurrentDictionary<string, HashSet<RepoPath>> _cache = new(StringComparer.Ordinal);
    private readonly List<string> _cacheOrder = [];
    private readonly Lock _cacheLock = new();
    private volatile bool _tokenIndexLoaded;

    public SymbolReferenceFinder(RepoRoot root, SyntaxTokenIndex tokenIndex)
    {
        _root = root;
        _tokenIndex = tokenIndex;
    }

    /// <summary>Syntax token and inheritance index used to narrow symbol searches.</summary>
    internal SyntaxTokenIndex TokenIndex => _tokenIndex;

    /// <summary>
    ///     Indexes the baseline documents of <paramref name="projects"/> for later searches, for the background warm.
    ///     A search never waits for it: what it has not indexed yet is searched fully.
    /// </summary>
    internal Task WarmIndexAsync(Solution baseline, IEnumerable<Project> projects, CancellationToken cancellationToken) =>
        _tokenIndex.EnsureIndexedAsync(baseline, projects, cancellationToken);

    /// <summary>Persists the search index when the background warm changed it.</summary>
    internal void SaveIndex() => _tokenIndex.SaveIfDirty("baseline");

    /// <summary>How many reference sets are kept.</summary>
    public int CachedSets => _cache.Count;

    /// <summary>Reads the persisted search entries once; later searches reuse what they hold.</summary>
    internal void EnsureIndexLoaded()
    {
        if (!_tokenIndexLoaded)
        {
            _tokenIndex.TryLoad("baseline");
            _tokenIndexLoaded = true;
        }
    }

    /// <summary>
    ///     The files that use <paramref name="symbol"/>, and the files that declare its implementations or overrides
    ///     when <paramref name="includeImplementations"/> is set. The reference search is narrowed by the token index
    ///     and its set is kept in the cache.
    /// </summary>
    /// <param name="searchKey">The searched projects as one sorted key; a set depends on which projects were searched.</param>
    internal async Task<List<RepoPath>> FilesReferencingAsync(
        ISymbol symbol,
        bool includeImplementations,
        string searchKey,
        HashSet<ProjectId> searchedIds,
        ImmutableHashSet<Project> baselineProjects,
        ImmutableHashSet<Document> baselineDocuments,
        Generation generation,
        CancellationToken cancellationToken)
    {
        var baseline = generation.Baseline;
        var files = new List<RepoPath>();
        var candidateDocs = CandidateDocumentsForSymbol(symbol, baseline, searchedIds, baselineDocuments);
        files.AddRange(await ReferencingFilesAsync(symbol, searchKey, baseline, candidateDocs, generation, cancellationToken).ConfigureAwait(false));
        if (includeImplementations)
        {
            var implProjects = CandidateProjectsForImplementations(symbol, baseline, searchedIds, baselineProjects);
            if (!implProjects.IsEmpty)
                files.AddRange(Declarations(await SymbolFinder.FindImplementationsAsync(symbol, baseline, implProjects, cancellationToken).ConfigureAwait(false)));

            var overrideProjects = CandidateProjectsForOverrides(symbol, baseline, searchedIds, baselineProjects);
            if (!overrideProjects.IsEmpty)
                files.AddRange(Declarations(await SymbolFinder.FindOverridesAsync(symbol, baseline, overrideProjects, cancellationToken).ConfigureAwait(false)));
        }

        return files;
    }

    /// <summary>The files that declare a type deriving from <paramref name="type"/> or, for an interface, implementing it.</summary>
    internal async Task<List<RepoPath>> FilesDerivingAsync(
        INamedTypeSymbol type,
        HashSet<ProjectId> searchedIds,
        ImmutableHashSet<Project> baselineProjects,
        Generation generation,
        CancellationToken cancellationToken)
    {
        var baseline = generation.Baseline;
        var files = new List<RepoPath>();
        var derivedProjects = CandidateProjectsForDerived(type, baseline, searchedIds, baselineProjects);
        if (!derivedProjects.IsEmpty)
        {
            files.AddRange(Declarations(await SymbolFinder.FindDerivedClassesAsync(type, baseline, transitive: true, derivedProjects, cancellationToken).ConfigureAwait(false)));
            if (type.TypeKind == TypeKind.Interface)
                files.AddRange(Declarations(await SymbolFinder.FindImplementationsAsync(type, baseline, derivedProjects, cancellationToken).ConfigureAwait(false)));
        }

        return files;
    }

    /// <summary>
    ///     Each file reached by one of <paramref name="names"/>, with the index of the name that reached it, for a
    ///     change code can reach without naming a symbol a reference search returns.
    /// </summary>
    internal async Task<List<(RepoPath File, int Index)>> FilesNamingAsync(
        IReadOnlyList<(string Name, int Index)> names,
        IReadOnlyList<Project> projects,
        CancellationToken cancellationToken)
    {
        var textDocuments = new List<(TextDocument Document, string Path)>();
        foreach (var document in projects.SelectMany(p => p.Documents.Concat<TextDocument>(p.AdditionalDocuments)))
        {
            if (document.FilePath is { } filePath)
                textDocuments.Add((document, filePath));
        }

        var nameHits = new ConcurrentBag<(RepoPath File, int Index)>();
        await Parallel.ForEachAsync(
            textDocuments,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (item, ct) =>
            {
                var text = (await item.Document.GetTextAsync(ct).ConfigureAwait(false)).ToString();
                RepoPath? file = null;
                foreach (var (name, index) in names)
                {
                    if (!text.Contains(name, StringComparison.Ordinal))
                        continue;
                    file ??= _root.PathOf(item.Path);
                    nameHits.Add((file.Value, index));
                }
            }).ConfigureAwait(false);

        return nameHits.ToList();
    }

    internal static string SimpleMemberName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    internal static bool IsImplicitPatternMember(string name) =>
        name is "GetEnumerator" or "GetAsyncEnumerator" or "MoveNext" or "MoveNextAsync"
            or "Current" or "Dispose" or "DisposeAsync"
            or "GetAwaiter" or "IsCompleted" or "GetResult" or "OnCompleted" or "UnsafeOnCompleted"
            or "Deconstruct" or "Add" or "Slice" or "Length" or "Count"
            or "GetPinnableReference" or "PrintMembers" or "EqualityContract" or "<Clone>$";

    private ImmutableHashSet<Document> CandidateDocumentsForSymbol(
        ISymbol symbol,
        Solution baseline,
        HashSet<ProjectId> searchedIds,
        ImmutableHashSet<Document> allDocuments)
    {
        if (!TryGetSymbolFilterTokens(symbol, out var identifiers, out _))
            return allDocuments;

        var candidates = _tokenIndex.CandidateDocumentsSnapshot(baseline, identifiers, [], searchedIds);
        return WithUnindexedAndDeclaring(candidates, symbol, baseline, searchedIds, allDocuments);
    }

    private ImmutableHashSet<Project> CandidateProjectsForImplementations(
        ISymbol symbol,
        Solution baseline,
        HashSet<ProjectId> searchedIds,
        ImmutableHashSet<Project> allProjects)
    {
        if (!TryGetSymbolFilterTokens(symbol, out var identifiers, out var baseTypeNames))
            return allProjects;

        var candidates = _tokenIndex.CandidateDocumentsSnapshot(baseline, identifiers, baseTypeNames, searchedIds);
        return ProjectsForCandidates(candidates, allProjects);
    }

    private ImmutableHashSet<Project> CandidateProjectsForOverrides(
        ISymbol symbol,
        Solution baseline,
        HashSet<ProjectId> searchedIds,
        ImmutableHashSet<Project> allProjects)
    {
        if (!TryGetSymbolFilterTokens(symbol, out var identifiers, out _))
            return allProjects;

        var candidates = _tokenIndex.CandidateDocumentsSnapshot(baseline, identifiers, [], searchedIds);
        return ProjectsForCandidates(candidates, allProjects);
    }

    private ImmutableHashSet<Project> CandidateProjectsForDerived(
        INamedTypeSymbol type,
        Solution baseline,
        HashSet<ProjectId> searchedIds,
        ImmutableHashSet<Project> allProjects)
    {
        if (type.IsAnonymousType || type.IsTupleType || string.IsNullOrEmpty(type.Name))
            return allProjects;

        var candidates = _tokenIndex.CandidateDocumentsSnapshot(baseline, [], [type.Name], searchedIds);
        return ProjectsForCandidates(candidates, allProjects);
    }

    private static ImmutableHashSet<Document> WithUnindexedAndDeclaring(
        ImmutableHashSet<Document> candidates,
        ISymbol symbol,
        Solution baseline,
        HashSet<ProjectId> searchedIds,
        ImmutableHashSet<Document> allDocuments)
    {
        var builder = candidates.ToBuilder();
        foreach (var doc in allDocuments)
        {
            if (string.IsNullOrEmpty(doc.FilePath))
                builder.Add(doc);
        }

        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            if (baseline.GetDocument(syntaxRef.SyntaxTree) is { } doc && searchedIds.Contains(doc.Project.Id))
                builder.Add(doc);
        }

        return builder.ToImmutable();
    }

    private static ImmutableHashSet<Project> ProjectsForCandidates(
        ImmutableHashSet<Document> candidates,
        ImmutableHashSet<Project> allProjects)
    {
        var builder = ImmutableHashSet.CreateBuilder<Project>();
        foreach (var doc in candidates)
            builder.Add(doc.Project);
        foreach (var project in allProjects)
        {
            if (project.Documents.Any(d => string.IsNullOrEmpty(d.FilePath)))
                builder.Add(project);
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
                if (method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.SharedConstructor
                    or MethodKind.UserDefinedOperator or MethodKind.Conversion or MethodKind.BuiltinOperator or MethodKind.Destructor)
                    return false;

                // An extension method applies without naming it (a query expression, an awaited or iterated
                // receiver), so no token narrows its search.
                if (method.IsExtensionMethod)
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

            case IFieldSymbol field:
                // A field is always named where it is used.
                if (string.IsNullOrEmpty(field.Name))
                    return false;
                identifiers = [field.Name];
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
                // A kind the narrowing cannot decide is searched without narrowing, so no call site is missed.
                return false;
        }
    }

    private async Task<IEnumerable<RepoPath>> ReferencingFilesAsync(ISymbol symbol, string searchKey, Solution baseline, IImmutableSet<Document> documents, Generation generation, CancellationToken cancellationToken)
    {
        var key = generation.BaselineGeneration + "|" + (symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)) + "|" + searchKey;
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                _cacheOrder.Remove(key);
                _cacheOrder.Add(key);
                return cached;
            }
        }

        var references = await SymbolFinder.FindReferencesAsync(symbol, baseline, documents, cancellationToken).ConfigureAwait(false);
        var files = new HashSet<RepoPath>();
        // A file holds many references; its path is made once.
        foreach (var document in references.SelectMany(r => r.Locations).Select(l => l.Document).DistinctBy(d => d.Id))
        {
            if (document.FilePath is { } file)
                files.Add(_root.PathOf(file));
        }

        lock (_cacheLock)
        {
            _cache[key] = files;
            _cacheOrder.Remove(key);
            _cacheOrder.Add(key);
            while (_cache.Count > MaxCacheEntries && _cacheOrder.Count > 0)
            {
                var oldest = _cacheOrder[0];
                _cacheOrder.RemoveAt(0);
                _cache.TryRemove(oldest, out _);
            }
        }

        return files;
    }

    private IEnumerable<RepoPath> Declarations(IEnumerable<ISymbol> symbols)
    {
        foreach (var location in symbols.SelectMany(s => s.Locations))
        {
            if (!location.IsInSource)
                continue;
            if (location.SourceTree?.FilePath is not { } file || string.IsNullOrEmpty(file))
                continue;
            yield return _root.PathOf(file);
        }
    }
}
