using System.Collections.Concurrent;
using Fuse.Paths;
using Fuse.Testing.Selection;
using Microsoft.CodeAnalysis;
namespace Fuse.Testing.Caching;

/// <summary>
///     Queries dynamic per-method, per-type and per-file test coverage mappings under
///     <c>SharedStateDirectory/test-maps/</c>. Populated from recorded coverage and from
///     ingested .NET 11 Microsoft.Testing.Platform <c>--collect-test-map</c> output files.
/// </summary>
internal sealed partial class DynamicTestMap
{
    private static readonly Lock _liveLock = new();
    private static readonly List<WeakReference<DynamicTestMap>> _live = [];

    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, CoverageMapStore.ProjectMap> _maps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _loadedStamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly CoverageMapStore _store;

    public DynamicTestMap(string sharedStateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedStateDirectory);
        _store = new CoverageMapStore(sharedStateDirectory);
        lock (_liveLock)
        {
            for (var i = _live.Count - 1; i >= 0; i--)
            {
                if (!_live[i].TryGetTarget(out _))
                    _live.RemoveAt(i);
            }

            _live.Add(new WeakReference<DynamicTestMap>(this));
        }
    }

    /// <summary>Directory storing dynamic test coverage maps for <paramref name="sharedStateDirectory"/>.</summary>
    public static string DirectoryFor(string sharedStateDirectory) => CoverageMapStore.DirectoryFor(sharedStateDirectory);

    /// <summary>Directory holding the test maps the client's platform runs left for the next plan to ingest.</summary>
    public static string IncomingDirectoryFor(string sharedStateDirectory) => CoverageMapStore.IncomingDirectoryFor(sharedStateDirectory);

    /// <summary>Coverage hits collected for one executed test method.</summary>
    internal sealed record CoverageHits(IReadOnlyCollection<string> Methods, IReadOnlyCollection<string> Types, IReadOnlyCollection<string> Files);

    /// <summary>True when at least one coverage mapping has been recorded or stored on disk.</summary>
    public bool HasAnyMapping
    {
        get
        {
            RefreshFromDisk();
            return !_maps.IsEmpty;
        }
    }

    /// <summary>
    ///     Drops this map's in-memory project mappings, keeping what is on disk, so an idle engine returns that
    ///     memory; the next lookup reads the stored mappings again.
    /// </summary>
    /// <returns>How many project mappings were dropped.</returns>
    public int ReleaseIdle()
    {
        lock (_gate)
        {
            var dropped = _maps.Count;
            _maps.Clear();
            _loadedStamps.Clear();
            return dropped;
        }
    }

    /// <summary>
    ///     Releases every live dynamic map's in-memory mappings, keeping what is on disk, so an idle engine
    ///     returns that memory.
    /// </summary>
    /// <returns>How many project mappings were dropped across every live map.</returns>
    public static int ReleaseIdleAll()
    {
        List<DynamicTestMap> targets;
        lock (_liveLock)
        {
            targets = [];
            for (var i = _live.Count - 1; i >= 0; i--)
            {
                if (_live[i].TryGetTarget(out var target))
                    targets.Add(target);
                else
                    _live.RemoveAt(i);
            }
        }

        var dropped = 0;
        foreach (var target in targets)
            dropped += target.ReleaseIdle();
        return dropped;
    }

    /// <summary>
    ///     Records coverage hits observed for one test project, replacing previous hits
    ///     for the tests present in <paramref name="hitsByTestId"/> while preserving hits for other tests in the project.
    /// </summary>
    public void RecordRunCoverage(string testProjectPath, RepoRoot root, IReadOnlyDictionary<string, CoverageHits> hitsByTestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(testProjectPath);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(hitsByTestId);
        if (hitsByTestId.Count == 0)
            return;

        var normalizedProject = NormalizeProjectPath(testProjectPath, root);
        lock (_gate)
        {
            _store.RefreshLocked(_maps, _loadedStamps);
            var map = _maps.GetOrAdd(normalizedProject, _ => new CoverageMapStore.ProjectMap(normalizedProject));
            var executedTests = new HashSet<string>(hitsByTestId.Keys, StringComparer.Ordinal);
            RemoveTestsFromMap(map, executedTests);

            foreach (var (testId, hits) in hitsByTestId)
            {
                if (string.IsNullOrWhiteSpace(testId))
                    continue;
                foreach (var method in hits.Methods)
                    CoverageMapStore.AddEntry(map.Methods, CoverageMapStore.NormalizeKey(method), testId);
                foreach (var type in hits.Types)
                    CoverageMapStore.AddEntry(map.Types, CoverageMapStore.NormalizeKey(type), testId);
                foreach (var file in hits.Files)
                    CoverageMapStore.AddEntry(map.Files, CoverageMapStore.NormalizeRelativeFile(file, root), testId);
            }

            _store.SaveLocked(map, _loadedStamps);
        }
    }

    /// <summary>Records explicit <c>methodKey -&gt; testIds</c> (and optional type/file) mappings for a test project.</summary>
    public void RecordMappings(
        string testProjectPath,
        RepoRoot root,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? methodToTests = null,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? typeToTests = null,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? fileToTests = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(testProjectPath);
        ArgumentNullException.ThrowIfNull(root);

        var normalizedProject = NormalizeProjectPath(testProjectPath, root);
        lock (_gate)
        {
            _store.RefreshLocked(_maps, _loadedStamps);
            var map = _maps.GetOrAdd(normalizedProject, _ => new CoverageMapStore.ProjectMap(normalizedProject));
            if (methodToTests is not null)
            {
                foreach (var (key, tests) in methodToTests)
                {
                    foreach (var testId in tests)
                        CoverageMapStore.AddEntry(map.Methods, CoverageMapStore.NormalizeKey(key), testId);
                }
            }

            if (typeToTests is not null)
            {
                foreach (var (key, tests) in typeToTests)
                {
                    foreach (var testId in tests)
                        CoverageMapStore.AddEntry(map.Types, CoverageMapStore.NormalizeKey(key), testId);
                }
            }

            if (fileToTests is not null)
            {
                foreach (var (key, tests) in fileToTests)
                {
                    foreach (var testId in tests)
                        CoverageMapStore.AddEntry(map.Files, CoverageMapStore.NormalizeRelativeFile(key, root), testId);
                }
            }

            _store.SaveLocked(map, _loadedStamps);
        }
    }

    /// <summary>Ingests a .NET 11 MTP <c>--collect-test-map</c> file into the dynamic map store.</summary>
    public async Task<int> IngestMtpTestMapAsync(string filePath, string testProjectPath, RepoRoot root, CancellationToken cancellationToken)
    {
        var content = await CoverageMapStore.ReadIngestFileAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (content is null)
            return 0;
        return IngestMtpTestMapContent(content, testProjectPath, root);
    }

    /// <summary>Discovers and ingests any MTP test-map files generated under <paramref name="searchDirectory"/>.</summary>
    public async Task<int> DiscoverAndIngestFromDirectoryAsync(string searchDirectory, string testProjectPath, RepoRoot root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(searchDirectory))
            return 0;

        var total = 0;
        foreach (var file in CoverageMapStore.TestMapFiles(searchDirectory))
        {
            total += await IngestMtpTestMapAsync(file, testProjectPath, root, cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    /// <summary>
    ///     Ingests the test maps the client's platform runs left in the incoming folder, each in a subfolder named
    ///     for its test project, and removes every subfolder attempted. Maps are advisory: a file that cannot be
    ///     read or parsed is dropped with its folder, never fails the plan.
    /// </summary>
    /// <returns>How many mappings were ingested.</returns>
    public async Task<int> IngestIncomingAsync(RepoRoot root, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        var folders = _store.ListIncomingFolders();
        var total = 0;
        foreach (var folder in folders)
        {
            try
            {
                CoverageMapStore.DropOversizedMaps(folder);
                if (CoverageMapStore.ProjectOfIncomingFolder(folder) is { } project)
                    total += await DiscoverAndIngestFromDirectoryAsync(folder, project, root, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A map that cannot be ingested is dropped with its folder below; the static selection stands alone.
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A folder that cannot be removed is attempted again by the next plan.
                }
            }
        }

        return total;
    }

    /// <summary>
    ///     Parses and stores mappings from a .NET 11 MTP <c>--collect-test-map</c> payload (supporting dictionary,
    ///     per-test entry array, and TSV formats). Returns how many mappings were ingested.
    /// </summary>
    public int IngestMtpTestMapContent(string content, string testProjectPath, RepoRoot root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(testProjectPath);
        ArgumentNullException.ThrowIfNull(root);
        if (string.IsNullOrWhiteSpace(content))
            return 0;

        var methods = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        var types = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        var files = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        var count = CoverageMapStore.ParseContent(content, root, methods, types, files);
        if (count > 0)
            RecordMappings(testProjectPath, root, methods, types, files);
        return count;
    }

    /// <summary>
    ///     Resolves exact mapped tests for <paramref name="symbol"/> (or its declaring type / source file when method-level
    ///     hits are absent). Returns false when the declaration has no dynamic mapping so callers fall back to static rules.
    /// </summary>
    public bool TryResolveForSymbol(ISymbol symbol, string? sourceRelativePath, RepoRoot root, out IReadOnlyDictionary<string, IReadOnlySet<string>> testsByProject)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(root);
        RefreshFromDisk();

        var matches = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (_maps.IsEmpty)
        {
            testsByProject = new Dictionary<string, IReadOnlySet<string>>();
            return false;
        }

        var methodKeys = SymbolMethodKeys(symbol);
        var typeKeys = SymbolTypeKeys(symbol);
        var normalizedFile = sourceRelativePath is null ? null : CoverageMapStore.NormalizeRelativeFile(sourceRelativePath, root);

        foreach (var (project, map) in _maps)
        {
            var foundForProject = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in methodKeys)
            {
                if (map.Methods.TryGetValue(key, out var tests))
                    foundForProject.UnionWith(tests);
            }

            if (foundForProject.Count == 0 && symbol is INamedTypeSymbol or IFieldSymbol or IEventSymbol)
            {
                foreach (var key in typeKeys)
                {
                    if (map.Types.TryGetValue(key, out var tests))
                        foundForProject.UnionWith(tests);
                }
            }

            if (foundForProject.Count == 0 && normalizedFile is not null && map.Files.TryGetValue(normalizedFile, out var fileTests))
                foundForProject.UnionWith(fileTests);

            if (foundForProject.Count > 0)
                matches[project] = foundForProject;
        }

        testsByProject = matches.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.OrdinalIgnoreCase);
        return matches.Count > 0;
    }

    /// <summary>Resolves dynamic test hits by type name or source file for class-level <see cref="TypeWalk"/> fallback.</summary>
    public bool TryResolveForType(string typeName, string? sourceRelativePath, RepoRoot root, out IReadOnlyDictionary<string, IReadOnlySet<string>> testsByProject)
    {
        RefreshFromDisk();
        var matches = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (_maps.IsEmpty)
        {
            testsByProject = new Dictionary<string, IReadOnlySet<string>>();
            return false;
        }

        var normalizedType = CoverageMapStore.NormalizeKey(typeName);
        var normalizedFile = sourceRelativePath is null ? null : CoverageMapStore.NormalizeRelativeFile(sourceRelativePath, root);
        foreach (var (project, map) in _maps)
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            if (normalizedType.Length > 0 && map.Types.TryGetValue(normalizedType, out var typeTests))
                found.UnionWith(typeTests);
            if (found.Count == 0 && normalizedFile is not null && map.Files.TryGetValue(normalizedFile, out var fileTests))
                found.UnionWith(fileTests);
            if (found.Count > 0)
                matches[project] = found;
        }

        testsByProject = matches.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.OrdinalIgnoreCase);
        return matches.Count > 0;
    }

    /// <summary>Returns canonical lookup keys for a Roslyn symbol matching both documentation IDs and CLR reflection names.</summary>
    internal static IReadOnlyList<string> SymbolMethodKeys(ISymbol symbol)
    {
        var keys = new List<string>(4);
        if (symbol.GetDocumentationCommentId() is { Length: > 0 } docId)
            keys.Add(docId);

        if (symbol is IMethodSymbol method)
        {
            var typePrefix = MetadataTypeName(method.ContainingType);
            if (typePrefix.Length > 0)
            {
                keys.Add($"{typePrefix}.{method.Name}");
                if (method.MethodKind == MethodKind.PropertyGet && method.AssociatedSymbol is IPropertySymbol getProp)
                    keys.Add($"{typePrefix}.{getProp.Name}");
                else if (method.MethodKind == MethodKind.PropertySet && method.AssociatedSymbol is IPropertySymbol setProp)
                    keys.Add($"{typePrefix}.{setProp.Name}");
                else if (method.MethodKind == MethodKind.Constructor)
                    keys.Add($"{typePrefix}..ctor");
            }
        }
        else if (symbol is IPropertySymbol property)
        {
            var typePrefix = MetadataTypeName(property.ContainingType);
            if (typePrefix.Length > 0)
            {
                keys.Add($"{typePrefix}.{property.Name}");
                if (property.GetMethod is not null)
                    keys.Add($"{typePrefix}.get_{property.Name}");
                if (property.SetMethod is not null)
                    keys.Add($"{typePrefix}.set_{property.Name}");
            }
        }
        else if (symbol.ContainingType is { } containing)
        {
            var typePrefix = MetadataTypeName(containing);
            if (typePrefix.Length > 0)
                keys.Add($"{typePrefix}.{symbol.Name}");
        }

        return keys;
    }

    /// <summary>Returns canonical type lookup keys for <paramref name="symbol"/>.</summary>
    internal static IReadOnlyList<string> SymbolTypeKeys(ISymbol symbol)
    {
        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        if (type is null)
            return [];
        var name = MetadataTypeName(type);
        return name.Length == 0 ? [] : [name, "T:" + name];
    }

    private static string MetadataTypeName(INamedTypeSymbol? type)
    {
        if (type is null)
            return "";
        var nesting = new Stack<string>();
        for (var current = type; current is not null; current = current.ContainingType)
            nesting.Push(current.MetadataName);
        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } space ? space.ToDisplayString() + "." : "";
        return ns + string.Join('+', nesting);
    }

    private void RefreshFromDisk()
    {
        lock (_gate)
            _store.RefreshLocked(_maps, _loadedStamps);
    }

    private static string NormalizeProjectPath(string projectPath, RepoRoot root) =>
        root.Contains(projectPath) || !Path.IsPathRooted(projectPath) ? root.PathOf(projectPath).Relative : projectPath.Replace('\\', '/');

    private static void RemoveTestsFromMap(CoverageMapStore.ProjectMap map, HashSet<string> testIds)
    {
        PruneTests(map.Methods, testIds);
        PruneTests(map.Types, testIds);
        PruneTests(map.Files, testIds);
    }

    private static void PruneTests(Dictionary<string, HashSet<string>> dict, HashSet<string> testIds)
    {
        var empty = new List<string>();
        foreach (var (key, set) in dict)
        {
            set.ExceptWith(testIds);
            if (set.Count == 0)
                empty.Add(key);
        }

        foreach (var key in empty)
            dict.Remove(key);
    }
}
