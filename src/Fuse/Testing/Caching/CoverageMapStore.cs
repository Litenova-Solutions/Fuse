using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fuse.Paths;
namespace Fuse.Testing.Caching;

/// <summary>
///     Stored coverage map files and ingested test map payloads under <c>SharedStateDirectory/test-maps/</c>. Holds the
///     directory, the file format and the parsing of ingested files, so the dynamic map keeps lookup and merge.
/// </summary>
internal sealed partial class CoverageMapStore
{
    private const int FormatVersion = 1;
    internal const int MaxIncomingProjects = 64;
    internal const int MaxIncomingFileBytes = 8_000_000;

    private readonly string _directory;

    public CoverageMapStore(string sharedStateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedStateDirectory);
        _directory = DirectoryFor(sharedStateDirectory);
    }

    /// <summary>Directory holding stored coverage map files for <paramref name="sharedStateDirectory"/>.</summary>
    internal static string DirectoryFor(string sharedStateDirectory) => Path.Combine(sharedStateDirectory, "test-maps");

    /// <summary>Directory holding the test maps the client's platform runs left for the next plan to ingest.</summary>
    internal static string IncomingDirectoryFor(string sharedStateDirectory) => Path.Combine(DirectoryFor(sharedStateDirectory), "incoming");

    /// <summary>Directory holding the test maps the client's platform runs left for the next plan to ingest.</summary>
    internal string IncomingDirectory => Path.Combine(_directory, "incoming");

    /// <summary>Reads stored mappings newer than <paramref name="stamps"/> into <paramref name="maps"/>.</summary>
    internal void RefreshLocked(ConcurrentDictionary<string, ProjectMap> maps, Dictionary<string, DateTime> stamps)
    {
        if (!Directory.Exists(_directory))
            return;

        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var stamp = File.GetLastWriteTimeUtc(file);
                if (stamps.TryGetValue(file, out var loaded) && loaded == stamp)
                    continue;
                var json = File.ReadAllText(file);
                var stored = JsonSerializer.Deserialize(json, DynamicMapJson.Default.StoredProjectMap);
                if (stored is null || stored.Version != FormatVersion || string.IsNullOrWhiteSpace(stored.TestProject))
                    continue;
                var map = new ProjectMap(stored.TestProject);
                CopyDict(stored.Methods, map.Methods);
                CopyDict(stored.Types, map.Types);
                CopyDict(stored.Files, map.Files);
                maps[stored.TestProject] = map;
                stamps[file] = stamp;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }
    }

    /// <summary>Persists <paramref name="map"/> atomically and records its stamp in <paramref name="stamps"/>.</summary>
    internal void SaveLocked(ProjectMap map, Dictionary<string, DateTime> stamps)
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
            stamps[file] = File.GetLastWriteTimeUtc(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal string FileFor(string testProject)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(testProject)))[..16];
        var name = Path.GetFileNameWithoutExtension(testProject);
        return Path.Combine(_directory, $"{name}-{hash}.json");
    }

    /// <summary>Reads an ingest file, or null when it is missing.</summary>
    internal static async Task<string?> ReadIngestFileAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
            return null;
        return await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Test map files under <paramref name="searchDirectory"/> by file name.</summary>
    internal static IEnumerable<string> TestMapFiles(string searchDirectory)
    {
        foreach (var file in Directory.EnumerateFiles(searchDirectory, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (!name.Contains("testmap", StringComparison.OrdinalIgnoreCase) && !name.Contains("test-map", StringComparison.OrdinalIgnoreCase))
                continue;
            yield return file;
        }
    }

    /// <summary>Incoming project folders to attempt, bounded so one run cannot ingest without bound.</summary>
    internal List<string> ListIncomingFolders()
    {
        var incoming = IncomingDirectory;
        if (!Directory.Exists(incoming))
            return [];
        try
        {
            return [.. Directory.EnumerateDirectories(incoming).Take(MaxIncomingProjects)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The test project relative path a client-encoded incoming folder names, or null when it names none.</summary>
    internal static string? ProjectOfIncomingFolder(string folder)
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
    internal static void DropOversizedMaps(string folder)
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

    internal static string NormalizeRelativeFile(string path, RepoRoot root)
    {
        if (Path.IsPathRooted(path) && root.Contains(path))
            return root.PathOf(path).Relative;
        return path.Replace('\\', '/').TrimStart('.', '/');
    }

    internal static string NormalizeKey(string key) => key.Trim();

    internal static void AddEntry(Dictionary<string, HashSet<string>> target, string key, string testId)
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

    /// <summary>
    ///     Parses a .NET 11 MTP <c>--collect-test-map</c> payload (supporting dictionary, per-test entry array, and TSV
    ///     formats) into <paramref name="methods"/>, <paramref name="types"/> and <paramref name="files"/>. Returns how
    ///     many mappings were parsed.
    /// </summary>
    internal static int ParseContent(
        string content,
        RepoRoot root,
        Dictionary<string, IReadOnlyCollection<string>> methods,
        Dictionary<string, IReadOnlyCollection<string>> types,
        Dictionary<string, IReadOnlyCollection<string>> files)
    {
        if (string.IsNullOrWhiteSpace(content))
            return 0;

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

        return count;
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

    /// <summary>One test project's mapped tests by method, type and file.</summary>
    internal sealed class ProjectMap
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
