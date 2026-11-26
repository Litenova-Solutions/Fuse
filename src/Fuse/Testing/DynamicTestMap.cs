using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fuse.Paths;
using Microsoft.CodeAnalysis;

namespace Fuse.Testing;

/// <summary>
///     Persists and queries dynamic per-method, per-type and per-file test coverage mappings under
///     <c>SharedStateDirectory/test-maps/</c>. Populated from recorded coverage and from
///     ingested .NET 11 Microsoft.Testing.Platform <c>--collect-test-map</c> output files.
/// </summary>
internal sealed partial class DynamicTestMap
{
    private const int FormatVersion = 1;
    internal const int MaxIncomingProjects = 64;
    internal const int MaxIncomingFileBytes = 8_000_000;

    private static readonly Lock _liveLock = new();
    private static readonly List<WeakReference<DynamicTestMap>> _live = [];

    private readonly string _directory;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, ProjectMap> _maps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _loadedStamps = new(StringComparer.OrdinalIgnoreCase);

    public DynamicTestMap(string sharedStateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedStateDirectory);
        _directory = DirectoryFor(sharedStateDirectory);
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
    public static string DirectoryFor(string sharedStateDirectory) => Path.Combine(sharedStateDirectory, "test-maps");

    /// <summary>Directory holding the test maps the client's platform runs left for the next plan to ingest.</summary>
    public static string IncomingDirectoryFor(string sharedStateDirectory) => Path.Combine(DirectoryFor(sharedStateDirectory), "incoming");

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
            RefreshFromDiskLocked();
            var map = _maps.GetOrAdd(normalizedProject, _ => new ProjectMap(normalizedProject));
            var executedTests = new HashSet<string>(hitsByTestId.Keys, StringComparer.Ordinal);
            RemoveTestsFromMap(map, executedTests);

            foreach (var (testId, hits) in hitsByTestId)
            {
                if (string.IsNullOrWhiteSpace(testId))
                    continue;
                foreach (var method in hits.Methods)
                    AddEntry(map.Methods, NormalizeKey(method), testId);
                foreach (var type in hits.Types)
                    AddEntry(map.Types, NormalizeKey(type), testId);
                foreach (var file in hits.Files)
                    AddEntry(map.Files, NormalizeRelativeFile(file, root), testId);
            }

            SaveLocked(map);
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
            RefreshFromDiskLocked();
            var map = _maps.GetOrAdd(normalizedProject, _ => new ProjectMap(normalizedProject));
            if (methodToTests is not null)
            {
                foreach (var (key, tests) in methodToTests)
                {
                    foreach (var testId in tests)
                        AddEntry(map.Methods, NormalizeKey(key), testId);
                }
            }

            if (typeToTests is not null)
            {
                foreach (var (key, tests) in typeToTests)
                {
                    foreach (var testId in tests)
                        AddEntry(map.Types, NormalizeKey(key), testId);
                }
            }

            if (fileToTests is not null)
            {
                foreach (var (key, tests) in fileToTests)
                {
                    foreach (var testId in tests)
                        AddEntry(map.Files, NormalizeRelativeFile(key, root), testId);
                }
            }

            SaveLocked(map);
        }
    }

    /// <summary>Ingests a .NET 11 MTP <c>--collect-test-map</c> file into the dynamic map store.</summary>
    public async Task<int> IngestMtpTestMapAsync(string filePath, string testProjectPath, RepoRoot root, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
            return 0;
        var content = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        return IngestMtpTestMapContent(content, testProjectPath, root);
    }

    /// <summary>Discovers and ingests any MTP test-map files generated under <paramref name="searchDirectory"/>.</summary>
    public async Task<int> DiscoverAndIngestFromDirectoryAsync(string searchDirectory, string testProjectPath, RepoRoot root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(searchDirectory))
            return 0;

        var total = 0;
        foreach (var file in Directory.EnumerateFiles(searchDirectory, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (!name.Contains("testmap", StringComparison.OrdinalIgnoreCase) && !name.Contains("test-map", StringComparison.OrdinalIgnoreCase))
                continue;
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
        var incoming = Path.Combine(_directory, "incoming");
        if (!Directory.Exists(incoming))
            return 0;

        List<string> folders;
        try
        {
            folders = [.. Directory.EnumerateDirectories(incoming).Take(MaxIncomingProjects)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var total = 0;
        foreach (var folder in folders)
        {
            try
            {
                DropOversizedMaps(folder);
                if (ProjectOfIncomingFolder(folder) is { } project)
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
        var count = 0;

        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                count = ParseMtpJson(doc.RootElement, root, methods, types, files);
            }
            catch (JsonException)
            {
                return 0;
            }
        }
        else
        {
            count = ParseTsv(content, root, methods, files);
        }

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
        var normalizedFile = sourceRelativePath is null ? null : NormalizeRelativeFile(sourceRelativePath, root);

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

        var normalizedType = NormalizeKey(typeName);
        var normalizedFile = sourceRelativePath is null ? null : NormalizeRelativeFile(sourceRelativePath, root);
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
            RefreshFromDiskLocked();
    }

    private void RefreshFromDiskLocked()
    {
        if (!Directory.Exists(_directory))
            return;

        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var stamp = File.GetLastWriteTimeUtc(file);
                if (_loadedStamps.TryGetValue(file, out var loaded) && loaded == stamp)
                    continue;
                var json = File.ReadAllText(file);
                var stored = JsonSerializer.Deserialize(json, DynamicMapJson.Default.StoredProjectMap);
                if (stored is null || stored.Version != FormatVersion || string.IsNullOrWhiteSpace(stored.TestProject))
                    continue;
                var map = new ProjectMap(stored.TestProject);
                CopyDict(stored.Methods, map.Methods);
                CopyDict(stored.Types, map.Types);
                CopyDict(stored.Files, map.Files);
                _maps[stored.TestProject] = map;
                _loadedStamps[file] = stamp;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }
    }

    private void SaveLocked(ProjectMap map)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var file = FileFor(map.TestProject);
            var temp = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            var payload = new StoredProjectMap(
                FormatVersion,
                map.TestProject,
                DateTime.UtcNow,
                ExportDict(map.Methods),
                ExportDict(map.Types),
                ExportDict(map.Files));
            File.WriteAllText(temp, JsonSerializer.Serialize(payload, DynamicMapJson.Default.StoredProjectMap));
            File.Move(temp, file, overwrite: true);
            _loadedStamps[file] = File.GetLastWriteTimeUtc(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string FileFor(string testProject)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(testProject)))[..16];
        var name = Path.GetFileNameWithoutExtension(testProject);
        return Path.Combine(_directory, $"{name}-{hash}.json");
    }

    private static string NormalizeProjectPath(string projectPath, RepoRoot root) =>
        root.Contains(projectPath) || !Path.IsPathRooted(projectPath) ? root.PathOf(projectPath).Relative : projectPath.Replace('\\', '/');

    /// <summary>The test project relative path a client-encoded incoming folder names, or null when it names none.</summary>
    private static string? ProjectOfIncomingFolder(string folder)
    {
        var stem = Path.GetFileName(folder);
        if (string.IsNullOrWhiteSpace(stem))
            return null;
        try
        {
            var relative = Encoding.UTF8.GetString(Convert.FromHexString(stem));
            return string.IsNullOrWhiteSpace(relative) ? null : relative;
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Deletes the incoming files past the size bound, so one huge file cannot exhaust memory while read.</summary>
    private static void DropOversizedMaps(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            try
            {
                if (new FileInfo(file).Length > MaxIncomingFileBytes)
                    File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be measured is left for the ingest to attempt or drop.
            }
        }
    }

    private static string NormalizeRelativeFile(string path, RepoRoot root)
    {
        if (Path.IsPathRooted(path) && root.Contains(path))
            return root.PathOf(path).Relative;
        return path.Replace('\\', '/').TrimStart('.', '/');
    }

    private static string NormalizeKey(string key) => key.Trim();

    private static void AddEntry(Dictionary<string, HashSet<string>> target, string key, string testId)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(testId))
            return;
        if (!target.TryGetValue(key, out var set))
            target[key] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(testId);
    }

    private static void AddDictEntry(Dictionary<string, IReadOnlyCollection<string>> target, string key, string testId)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(testId))
            return;
        if (!target.TryGetValue(key, out var existing))
        {
            var set = new HashSet<string>(StringComparer.Ordinal) { testId };
            target[key] = set;
            return;
        }

        ((HashSet<string>)existing).Add(testId);
    }

    private static void RemoveTestsFromMap(ProjectMap map, HashSet<string> testIds)
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

    private static int ParseMtpJson(
        JsonElement rootElement,
        RepoRoot root,
        Dictionary<string, IReadOnlyCollection<string>> methods,
        Dictionary<string, IReadOnlyCollection<string>> types,
        Dictionary<string, IReadOnlyCollection<string>> files)
    {
        var count = 0;
        if (rootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in rootElement.EnumerateArray())
                count += ParseMtpArrayItem(item, root, methods, types, files);
            return count;
        }

        if (rootElement.ValueKind != JsonValueKind.Object)
            return 0;

        if (TryGetPropertyIgnoreCase(rootElement, "methods", out var methodsNode) && methodsNode.ValueKind == JsonValueKind.Object)
            count += ParseMapObject(methodsNode, methods, NormalizeKey);
        if (TryGetPropertyIgnoreCase(rootElement, "types", out var typesNode) && typesNode.ValueKind == JsonValueKind.Object)
            count += ParseMapObject(typesNode, types, NormalizeKey);
        if (TryGetPropertyIgnoreCase(rootElement, "files", out var filesNode) && filesNode.ValueKind == JsonValueKind.Object)
            count += ParseMapObject(filesNode, files, p => NormalizeRelativeFile(p, root));
        if (TryGetPropertyIgnoreCase(rootElement, "mappings", out var mappingsNode))
            count += ParseMappingsContainer(mappingsNode, root, methods, types, files);
        if (TryGetPropertyIgnoreCase(rootElement, "entries", out var entriesNode))
            count += ParseMappingsContainer(entriesNode, root, methods, types, files);
        if (TryGetPropertyIgnoreCase(rootElement, "tests", out var testsNode))
            count += ParseTestsContainer(testsNode, root, methods, types, files);
        if (TryGetPropertyIgnoreCase(rootElement, "testMap", out var testMapNode))
            count += ParseMappingsContainer(testMapNode, root, methods, types, files);

        // Support bare dictionary `{ "src/File.cs": ["TestA", "TestB"] }` or `{ "Namespace.Type.Method": ["TestA"] }`.
        if (count == 0)
        {
            foreach (var prop in rootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array)
                    continue;
                var target = LooksLikeFilePath(prop.Name) ? files : methods;
                var key = LooksLikeFilePath(prop.Name) ? NormalizeRelativeFile(prop.Name, root) : NormalizeKey(prop.Name);
                foreach (var entry in prop.Value.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } testId)
                    {
                        AddDictEntry(target, key, testId);
                        count++;
                    }
                }
            }
        }

        return count;
    }

    private static int ParseMappingsContainer(
        JsonElement node,
        RepoRoot root,
        Dictionary<string, IReadOnlyCollection<string>> methods,
        Dictionary<string, IReadOnlyCollection<string>> types,
        Dictionary<string, IReadOnlyCollection<string>> files)
    {
        var count = 0;
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                count += ParseMtpArrayItem(item, root, methods, types, files);
            return count;
        }

        if (node.ValueKind != JsonValueKind.Object)
            return 0;

        foreach (var prop in node.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array)
                continue;
            var target = LooksLikeFilePath(prop.Name) ? files : methods;
            var key = LooksLikeFilePath(prop.Name) ? NormalizeRelativeFile(prop.Name, root) : NormalizeKey(prop.Name);
            foreach (var item in prop.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } testId)
                {
                    AddDictEntry(target, key, testId);
                    count++;
                }
            }
        }

        return count;
    }

    private static int ParseTestsContainer(
        JsonElement node,
        RepoRoot root,
        Dictionary<string, IReadOnlyCollection<string>> methods,
        Dictionary<string, IReadOnlyCollection<string>> types,
        Dictionary<string, IReadOnlyCollection<string>> files)
    {
        var count = 0;
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                count += ParseMtpArrayItem(item, root, methods, types, files);
            return count;
        }

        if (node.ValueKind != JsonValueKind.Object)
            return 0;

        foreach (var prop in node.EnumerateObject())
        {
            var testId = prop.Name;
            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var covered in prop.Value.EnumerateArray())
                {
                    if (covered.ValueKind != JsonValueKind.String || covered.GetString() is not { Length: > 0 } value)
                        continue;
                    var target = LooksLikeFilePath(value) ? files : methods;
                    AddDictEntry(target, LooksLikeFilePath(value) ? NormalizeRelativeFile(value, root) : NormalizeKey(value), testId);
                    count++;
                }
            }
            else if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                count += ParseTestObject(testId, prop.Value, root, methods, types, files);
            }
        }

        return count;
    }

    private static int ParseMtpArrayItem(
        JsonElement item,
        RepoRoot root,
        Dictionary<string, IReadOnlyCollection<string>> methods,
        Dictionary<string, IReadOnlyCollection<string>> types,
        Dictionary<string, IReadOnlyCollection<string>> files)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return 0;

        if (TryGetString(item, "testId", out var testId)
            || TryGetString(item, "test", out testId)
            || TryGetString(item, "fullyQualifiedName", out testId)
            || TryGetString(item, "name", out testId))
        {
            return ParseTestObject(testId, item, root, methods, types, files);
        }

        if ((TryGetString(item, "method", out var key)
             || TryGetString(item, "methodKey", out key)
             || TryGetString(item, "symbol", out key)
             || TryGetString(item, "file", out key)
             || TryGetString(item, "source", out key)
             || TryGetString(item, "path", out key))
            && (TryGetPropertyIgnoreCase(item, "tests", out var testsNode) || TryGetPropertyIgnoreCase(item, "testIds", out testsNode))
            && testsNode.ValueKind == JsonValueKind.Array)
        {
            var count = 0;
            var target = LooksLikeFilePath(key) ? files : methods;
            var normalized = LooksLikeFilePath(key) ? NormalizeRelativeFile(key, root) : NormalizeKey(key);
            foreach (var test in testsNode.EnumerateArray())
            {
                if (test.ValueKind == JsonValueKind.String && test.GetString() is { Length: > 0 } id)
                {
                    AddDictEntry(target, normalized, id);
                    count++;
                }
            }

            return count;
        }

        return 0;
    }

    private static int ParseTestObject(
        string testId,
        JsonElement item,
        RepoRoot root,
        Dictionary<string, IReadOnlyCollection<string>> methods,
        Dictionary<string, IReadOnlyCollection<string>> types,
        Dictionary<string, IReadOnlyCollection<string>> files)
    {
        var count = 0;
        if (TryGetPropertyIgnoreCase(item, "methods", out var methodsArray) && methodsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in methodsArray.EnumerateArray())
            {
                if (m.ValueKind == JsonValueKind.String && m.GetString() is { Length: > 0 } value)
                {
                    AddDictEntry(methods, NormalizeKey(value), testId);
                    count++;
                }
            }
        }

        if (TryGetPropertyIgnoreCase(item, "types", out var typesArray) && typesArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in typesArray.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } value)
                {
                    AddDictEntry(types, NormalizeKey(value), testId);
                    count++;
                }
            }
        }

        if ((TryGetPropertyIgnoreCase(item, "files", out var filesArray) || TryGetPropertyIgnoreCase(item, "sources", out filesArray))
            && filesArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in filesArray.EnumerateArray())
            {
                if (f.ValueKind == JsonValueKind.String && f.GetString() is { Length: > 0 } value)
                {
                    AddDictEntry(files, NormalizeRelativeFile(value, root), testId);
                    count++;
                }
            }
        }

        return count;
    }

    private static int ParseMapObject(JsonElement obj, Dictionary<string, IReadOnlyCollection<string>> target, Func<string, string> normalizeKey)
    {
        var count = 0;
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array)
                continue;
            var key = normalizeKey(prop.Name);
            foreach (var entry in prop.Value.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } testId)
                {
                    AddDictEntry(target, key, testId);
                    count++;
                }
            }
        }

        return count;
    }

    private static int ParseTsv(
        string content,
        RepoRoot root,
        Dictionary<string, IReadOnlyCollection<string>> methods,
        Dictionary<string, IReadOnlyCollection<string>> files)
    {
        var count = 0;
        foreach (var raw in content.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var parts = line.Split('\t', 2);
            if (parts.Length != 2)
                continue;
            var key = parts[0].Trim();
            var tests = parts[1].Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var target = LooksLikeFilePath(key) ? files : methods;
            var normalized = LooksLikeFilePath(key) ? NormalizeRelativeFile(key, root) : NormalizeKey(key);
            foreach (var testId in tests)
            {
                AddDictEntry(target, normalized, testId);
                count++;
            }
        }

        return count;
    }

    private static bool LooksLikeFilePath(string value) =>
        value.Contains('/') || value.Contains('\\') || value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        if (TryGetPropertyIgnoreCase(obj, name, out var prop) && prop.ValueKind == JsonValueKind.String && prop.GetString() is { Length: > 0 } s)
        {
            value = s;
            return true;
        }

        value = "";
        return false;
    }

    private static void CopyDict(Dictionary<string, string[]>? source, Dictionary<string, HashSet<string>> destination)
    {
        if (source is null)
            return;
        foreach (var (key, tests) in source)
        {
            foreach (var test in tests)
                AddEntry(destination, key, test);
        }
    }

    private static Dictionary<string, string[]> ExportDict(Dictionary<string, HashSet<string>> source) =>
        source.ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(t => t, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

    private sealed class ProjectMap
    {
        public ProjectMap(string testProject) => TestProject = testProject;

        public string TestProject { get; }

        public Dictionary<string, HashSet<string>> Methods { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, HashSet<string>> Types { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, HashSet<string>> Files { get; } = new(StringComparer.Ordinal);
    }

    private sealed record StoredProjectMap(
        int Version,
        string TestProject,
        DateTime UpdatedUtc,
        Dictionary<string, string[]> Methods,
        Dictionary<string, string[]> Types,
        Dictionary<string, string[]> Files);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(StoredProjectMap))]
    private sealed partial class DynamicMapJson : JsonSerializerContext;
}
