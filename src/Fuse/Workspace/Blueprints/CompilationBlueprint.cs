using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Fuse.Graph;
using Fuse.Paths;
namespace Fuse.Workspace.Blueprints;

/// <summary>
///     Serializable compilation inputs for a repository's projects, extracted from MSBuild once per project-file
///     state and stored under <c>SharedStateDirectory/blueprints/&lt;key&gt;.json</c>. A resident engine loads projects
///     from the blueprint into a lean <c>AdhocWorkspace</c> instead of holding an <c>MSBuildWorkspace</c> in memory,
///     and an unrestored secondary worktree with matching project/restore inputs maps its sources onto a restored
///     worktree's blueprint without running <c>dotnet restore</c>.
/// </summary>
internal sealed partial class CompilationBlueprint
{
    internal const int FormatVersion = 3;

    private const string RootTokenNative = "${FUSE_REPO_ROOT}";
    private const string RootTokenForward = "${FUSE_REPO_ROOT_FWD}";

    private static readonly string[] AutoImportNames =
        ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "packages.lock.json", "global.json"];

    private const int WriteLockAttempts = 20;
    private const int WriteLockRetryMs = 50;

    /// <summary>
    ///     The blueprint file read last for each blueprints directory, with the length and write time it had when it
    ///     was read. Every load validates the cache by reading the file for its key, so without this the same file is
    ///     read and deserialized again on every load; the stamp check answers the same for an unchanged file.
    /// </summary>
    private static readonly ConcurrentDictionary<string, BlueprintFileCacheEntry> _fileCache = new();

    /// <summary>
    ///     The masked hash read last for each restore output path, with the length and write time it had when it was
    ///     hashed. Coverage checks hash every project's restore output on every load; the stamp check answers the same
    ///     for an unchanged file. Only successful hashes are kept: a file that cannot be hashed is tried again.
    /// </summary>
    private static readonly ConcurrentDictionary<string, AssetsCacheEntry> _assetsCache = new();

    /// <summary>The blueprint file read last for one blueprints directory: its path, and the stamp the read saw.</summary>
    private sealed record BlueprintFileCacheEntry(string Path, long Length, DateTime WrittenUtc, CompilationBlueprint Blueprint);

    /// <summary>The masked hash read last for one restore output path, and the stamp the read saw.</summary>
    private sealed record AssetsCacheEntry(long Length, DateTime WrittenUtc, string Hash);

    /// <summary>
    ///     How old a file's write time must be before a cached stamp is trusted. A file written within this window
    ///     is read again, so a rewrite the timestamp cannot tell apart from the cached read never answers stale.
    /// </summary>
    private static readonly TimeSpan SettledWindow = TimeSpan.FromSeconds(2);

    /// <summary>True when <paramref name="writtenUtc"/> is old enough that no live writer can still hold the file.</summary>
    private static bool IsSettled(DateTime writtenUtc) => DateTime.UtcNow - writtenUtc >= SettledWindow;

    public CompilationBlueprint(
        int version,
        string build,
        string key,
        string sourceRoot,
        Dictionary<string, string> inputHashes,
        Dictionary<string, string> assetsHashes,
        List<BlueprintProject> projects,
        Dictionary<string, List<string>>? knownSources = null)
    {
        Version = version;
        Build = build;
        Key = key;
        SourceRoot = sourceRoot;
        InputHashes = inputHashes;
        AssetsHashes = assetsHashes;
        Projects = projects;
        KnownSources = knownSources ?? new Dictionary<string, List<string>>(StringComparer.Ordinal);
    }

    /// <summary>Schema version of this blueprint file.</summary>
    public int Version { get; }

    /// <summary>Fuse/Roslyn build stamp that produced this blueprint.</summary>
    public string Build { get; }

    /// <summary>Content key covering project files, imports, props, targets and lock files.</summary>
    public string Key { get; }

    /// <summary>Canonical root path of the restored worktree that produced this blueprint.</summary>
    public string SourceRoot { get; }

    /// <summary>SHA-256 hashes of every project file, MSBuild import and auto-import in the repository, keyed by relative path.</summary>
    public Dictionary<string, string> InputHashes { get; }

    /// <summary>Masked SHA-256 hashes of each evaluated project's <c>project.assets.json</c>, keyed by relative path.</summary>
    public Dictionary<string, string> AssetsHashes { get; }

    /// <summary>Compiler inputs for each evaluated project/target-framework pair.</summary>
    public List<BlueprintProject> Projects { get; }

    /// <summary>
    ///     Every source file MSBuild evaluated for each project file at extraction time, by stored project path. A
    ///     working-tree source the graph names that is not in here was added after the extraction, so the load adds
    ///     it; one that is in here but missing from a variant's compile list was excluded for that variant on
    ///     purpose, so the load leaves it out. Blueprints written before this map existed carry an empty one, and
    ///     the load falls back to the union of the variants' compile lists.
    /// </summary>
    public Dictionary<string, List<string>> KnownSources { get; }

    /// <summary>Current Fuse/Roslyn build stamp used to invalidate stale blueprints across upgrades.</summary>
    public static string CurrentBuild =>
        (typeof(CompilationBlueprint).Assembly.GetName().Version?.ToString() ?? "")
        + "|"
        + (typeof(Microsoft.CodeAnalysis.Compilation).Assembly.GetName().Version?.ToString() ?? "");

    /// <summary>Directory holding shared blueprints under <paramref name="sharedStateDirectory"/>.</summary>
    public static string DirectoryFor(string sharedStateDirectory) =>
        Path.Combine(sharedStateDirectory, "blueprints");

    /// <summary>File path for a blueprint keyed by <paramref name="key"/>.</summary>
    public static string FileFor(string sharedStateDirectory, string key) =>
        Path.Combine(DirectoryFor(sharedStateDirectory), key + ".json");

    /// <summary>
    ///     Computes the deterministic key for <paramref name="graph"/> in <paramref name="root"/> from project files,
    ///     MSBuild imports, props, targets, lock files and <c>global.json</c>. Unrelated source edits keep the same key,
    ///     and matching worktrees produce the exact same key.
    /// </summary>
    public static string? ComputeKey(RepoRoot root, RepoGraph graph) =>
        ComputeInputs(root, graph)?.Key;

    /// <summary>Computes both the key and the full map of input hashes for <paramref name="graph"/>.</summary>
    internal static (string Key, Dictionary<string, string> InputHashes)? ComputeInputs(RepoRoot root, RepoGraph graph)
    {
        try
        {
            var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var project in graph.Projects.OrderBy(p => p.Path.Relative, StringComparer.Ordinal))
            {
                if (!File.Exists(project.Path.Absolute))
                    return null;
                inputs[project.Path.Relative] = HashFile(project.Path.Absolute);
                foreach (var input in project.EvaluationInputs.OrderBy(p => p.Relative, StringComparer.Ordinal))
                {
                    if (inputs.ContainsKey(input.Relative))
                        continue;
                    if (!File.Exists(input.Absolute))
                        return null;
                    inputs[input.Relative] = HashFile(input.Absolute);
                }
            }

            foreach (var relative in AutoImports(root, graph))
            {
                if (inputs.ContainsKey(relative))
                    continue;
                var absolute = root.PathOf(relative).Absolute;
                if (File.Exists(absolute))
                    inputs[relative] = HashFile(absolute);
            }

            var lines = new List<string>(inputs.Count + 2)
            {
                FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CurrentBuild,
            };
            foreach (var (relative, hash) in inputs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                lines.Add(relative + "=" + hash);

            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
            return (key, inputs);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Loads the cached blueprint for <paramref name="root"/> and <paramref name="graph"/> when valid, or null.</summary>
    public static CompilationBlueprint? TryLoad(RepoRoot root, RepoGraph graph)
    {
        if (ComputeInputs(root, graph) is not var (key, inputs))
            return null;
        var loaded = TryReadFile(FileFor(root.SharedStateDirectory, key));
        if (loaded is null || !loaded.MatchesInputs(key, inputs))
            return null;
        return loaded;
    }

    /// <summary>Loads a blueprint directly from <paramref name="filePath"/>, or null when missing or invalid.</summary>
    public static CompilationBlueprint? TryReadFile(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
                return null;
            var directory = Path.GetDirectoryName(filePath) ?? "";
            if (_fileCache.TryGetValue(directory, out var cached)
                && string.Equals(cached.Path, filePath, StringComparison.Ordinal)
                && cached.Length == info.Length
                && cached.WrittenUtc == info.LastWriteTimeUtc
                && IsSettled(info.LastWriteTimeUtc))
                return cached.Blueprint;
            var stamp = (info.Length, info.LastWriteTimeUtc);
            var json = ReadText(filePath);
            var blueprint = JsonSerializer.Deserialize(json, BlueprintJson.Default.CompilationBlueprint);
            if (blueprint is null || blueprint.Version != FormatVersion || !string.Equals(blueprint.Build, CurrentBuild, StringComparison.Ordinal))
                return null;
            if (blueprint.Projects is null || blueprint.InputHashes is null || blueprint.AssetsHashes is null || string.IsNullOrEmpty(blueprint.SourceRoot))
                return null;
            info.Refresh();
            if (info.Exists && info.Length == stamp.Length && info.LastWriteTimeUtc == stamp.LastWriteTimeUtc)
                _fileCache[directory] = new BlueprintFileCacheEntry(filePath, stamp.Length, stamp.LastWriteTimeUtc, blueprint);
            return blueprint;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>True when this blueprint's key and input hashes match <paramref name="key"/> and <paramref name="inputs"/>.</summary>
    public bool MatchesInputs(string key, IReadOnlyDictionary<string, string> inputs)
    {
        if (!string.Equals(Key, key, StringComparison.Ordinal) || InputHashes.Count != inputs.Count)
            return false;
        foreach (var (path, hash) in inputs)
        {
            if (!InputHashes.TryGetValue(path, out var existing) || !string.Equals(existing, hash, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>
    ///     True when every project in <paramref name="closure"/> is present in this blueprint and its restore state is
    ///     valid for <paramref name="root"/> (either local <c>project.assets.json</c> hashes match, or
    ///     <paramref name="root"/> is an unrestored secondary worktree whose restored <see cref="SourceRoot"/> still
    ///     holds matching assets).
    /// </summary>
    public bool Covers(RepoRoot root, IEnumerable<ProjectNode> closure, bool allowZeroRestore)
    {
        var byPath = new HashSet<string>(Projects.Select(p => p.ProjectPath), StringComparer.Ordinal);
        var isSecondary = !root.PathOf(SourceRoot).Equals(root.PathOf(root.Path));
        foreach (var node in closure)
        {
            if (!byPath.Contains(node.Path.Relative))
                return false;
            if (!AssetsHashes.TryGetValue(node.AssetsFile.Relative, out var expectedAssets))
                return false;

            if (File.Exists(node.AssetsFile.Absolute))
            {
                if (!string.Equals(HashAssets(root, node.AssetsFile.Absolute), expectedAssets, StringComparison.Ordinal))
                    return false;
            }
            else
            {
                if (!allowZeroRestore || !isSecondary)
                    return false;
                var sourceAssets = Path.GetFullPath(Path.Combine(SourceRoot, node.AssetsFile.Relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(sourceAssets))
                    return false;
                var sourceRoot = RepoRoot.Find(SourceRoot) ?? root;
                if (!string.Equals(HashAssets(sourceRoot, sourceAssets), expectedAssets, StringComparison.Ordinal))
                    return false;
            }
        }

        return true;
    }

    /// <summary>Returns a new blueprint merging <paramref name="updatedProjects"/>, <paramref name="updatedAssets"/> and <paramref name="updatedKnownSources"/> into this blueprint.</summary>
    public CompilationBlueprint Merge(string sourceRoot, Dictionary<string, string> inputHashes, Dictionary<string, string> updatedAssets, IReadOnlyList<BlueprintProject> updatedProjects, IReadOnlyDictionary<string, List<string>>? updatedKnownSources = null)
    {
        var mergedAssets = new Dictionary<string, string>(AssetsHashes, StringComparer.Ordinal);
        foreach (var (path, hash) in updatedAssets)
            mergedAssets[path] = hash;

        var replacedPaths = new HashSet<string>(updatedProjects.Select(p => p.ProjectPath), StringComparer.Ordinal);
        var mergedProjects = Projects.Where(p => !replacedPaths.Contains(p.ProjectPath)).ToList();
        mergedProjects.AddRange(updatedProjects);
        mergedProjects.Sort((a, b) =>
        {
            var byPath = string.Compare(a.ProjectPath, b.ProjectPath, StringComparison.Ordinal);
            return byPath != 0 ? byPath : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });

        var mergedKnownSources = new Dictionary<string, List<string>>(KnownSources, StringComparer.Ordinal);
        if (updatedKnownSources is not null)
        {
            foreach (var path in replacedPaths)
                mergedKnownSources.Remove(path);
            foreach (var (path, sources) in updatedKnownSources)
            {
                if (replacedPaths.Contains(path))
                    mergedKnownSources[path] = sources;
            }
        }

        return new CompilationBlueprint(FormatVersion, CurrentBuild, Key, sourceRoot, inputHashes, mergedAssets, mergedProjects, mergedKnownSources);
    }

    /// <summary>Atomically writes this blueprint to <paramref name="filePath"/> using a staging file and directory write lock.</summary>
    public bool TryWrite(string filePath)
    {
        var staging = filePath + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        try
        {
            var directory = Path.GetDirectoryName(filePath)!;
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(this, BlueprintJson.Default.CompilationBlueprint);
            File.WriteAllText(staging, json);
            using var locked = AcquireWriteLock(directory);
            if (locked is null)
            {
                TryDelete(staging);
                return false;
            }

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(staging, filePath, overwrite: true);
                    return true;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            TryDelete(staging);
            return false;
        }
    }

    /// <summary>Deletes older cached blueprints under <paramref name="sharedStateDirectory"/>, keeping the 4 newest.</summary>
    public static void Prune(string sharedStateDirectory)
    {
        try
        {
            var directory = DirectoryFor(sharedStateDirectory);
            if (!Directory.Exists(directory))
                return;
            foreach (var file in Directory.EnumerateFiles(directory, "*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(4)
                .ToList())
            {
                TryDelete(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Encodes <paramref name="absolutePath"/> relative to <paramref name="root"/> when contained inside it, otherwise unchanged.</summary>
    internal static string StorePath(RepoRoot root, string absolutePath) =>
        root.Contains(absolutePath) ? root.PathOf(absolutePath).Relative : absolutePath;

    /// <summary>Resolves a stored path (repo-relative or external absolute) against <paramref name="root"/>.</summary>
    internal static string ResolvePath(RepoRoot root, string storedPath) =>
        Path.IsPathRooted(storedPath) ? storedPath : root.PathOf(storedPath).Absolute;

    /// <summary>Replaces spellings of <paramref name="root"/> inside generated text so a secondary worktree can materialize it under its own root.</summary>
    internal static string MaskRootText(RepoRoot root, string text) =>
        root.ReplaceRoot(text, RootTokenNative, RootTokenForward);

    /// <summary>Expands root tokens inside stored generated text back to <paramref name="root"/>'s path.</summary>
    internal static string UnmaskRootText(RepoRoot root, string text)
    {
        var native = root.Path;
        var forward = native.Replace('\\', '/');
        return text.Replace(RootTokenNative, native, StringComparison.Ordinal)
            .Replace(RootTokenForward, forward, StringComparison.Ordinal);
    }

    /// <summary>Computes the worktree-agnostic hash of <c>project.assets.json</c> at <paramref name="path"/>, or null when unreadable.</summary>
    internal static string? HashAssets(RepoRoot root, string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;
            if (_assetsCache.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.WrittenUtc == info.LastWriteTimeUtc && IsSettled(info.LastWriteTimeUtc))
                return cached.Hash;
            var stamp = (info.Length, info.LastWriteTimeUtc);
            string hash;
            if (JsonNode.Parse(ReadText(path)) is not { } assets)
                hash = HashFile(path);
            else
            {
                MaskWorktreePaths(assets, root);
                hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(assets.ToJsonString())));
            }

            info.Refresh();
            if (info.Exists && info.Length == stamp.Length && info.LastWriteTimeUtc == stamp.LastWriteTimeUtc)
                _assetsCache[path] = new AssetsCacheEntry(stamp.Length, stamp.LastWriteTimeUtc, hash);
            return hash;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Every MSBuild and NuGet auto-import of <paramref name="graph"/>'s projects: the well-known file names in
    ///     each project's directory and its ancestors up to the root, which is where MSBuild and NuGet look them up.
    ///     A same-named file with no project beneath it is never imported, so it stays out of the key.
    /// </summary>
    private static List<string> AutoImports(RepoRoot root, RepoGraph graph)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> seenDirs = [];
        foreach (var project in graph.Projects)
        {
            var directory = Path.GetDirectoryName(project.Path.Absolute);
            while (directory is not null && root.Contains(directory) && seenDirs.Add(directory))
            {
                foreach (var name in AutoImportNames)
                {
                    var candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate) && !PathRules.IsBuildOutput(root.Path, candidate))
                        found.Add(root.PathOf(candidate).Relative);
                }

                directory = Path.GetDirectoryName(directory);
            }
        }

        var result = found.ToList();
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(SharedRead.AllBytes(path)));

    private static string ReadText(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void MaskWorktreePaths(JsonNode node, RepoRoot root)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(pair => pair.Key).ToList())
            {
                if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && MaskedPath(text, root) is { } masked)
                    obj[key] = JsonValue.Create(masked);
                else if (obj[key] is { } child)
                    MaskWorktreePaths(child, root);
            }

            var pairs = obj.ToList();
            if (pairs.All(pair => MaskedPath(pair.Key, root) is null))
                return;
            obj.Clear();
            foreach (var pair in pairs)
                obj[MaskedPath(pair.Key, root) ?? pair.Key] = pair.Value;
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue value && value.TryGetValue<string>(out var text) && MaskedPath(text, root) is { } masked)
                    array[i] = JsonValue.Create(masked);
                else if (array[i] is { } child)
                    MaskWorktreePaths(child, root);
            }
        }
    }

    private static string? MaskedPath(string value, RepoRoot root) =>
        value.Length > root.Path.Length && root.Contains(value) ? root.PathOf(value).Relative : null;

    private static FileStream? AcquireWriteLock(string directory)
    {
        var path = Path.Combine(directory, "write.lock");
        for (var attempt = 0; attempt < WriteLockAttempts; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (File.Exists(path))
            {
                Thread.Sleep(WriteLockRetryMs);
            }
        }

        return null;
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>One document entry in a project blueprint, optionally carrying masked inline text for <c>obj/</c> files.</summary>
    internal sealed record BlueprintDocument(string Path, IReadOnlyList<string> Folders, string? InlineText = null);

    /// <summary>One project-to-project reference in a blueprint, preserving target framework identity for multi-targeted targets.</summary>
    internal sealed record BlueprintProjectReference(string TargetProjectPath, string TargetProjectName, IReadOnlyList<string> Aliases, bool EmbedInteropTypes);

    /// <summary>Exact compiler inputs for one evaluated Roslyn project (one target framework).</summary>
    /// <remarks>
    ///     <c>CryptoPublicKey</c> is the key bytes as hex: null when unset, empty when explicitly empty, so the load
    ///     restores the same state MSBuild gave. <c>CryptoKeyFile</c> is a stored path (see <see cref="StorePath"/>).
    ///     <c>StrongNameProvider</c> is the provider type's full name. Fields added after the first release keep
    ///     defaults, so blueprints written before them still read.
    /// </remarks>
    internal sealed record BlueprintProject(
        string ProjectPath,
        string Name,
        string AssemblyName,
        IReadOnlyList<BlueprintDocument> Compile,
        IReadOnlyList<string> ReferencePath,
        IReadOnlyList<string> Analyzer,
        IReadOnlyList<BlueprintDocument> AdditionalFiles,
        IReadOnlyList<BlueprintDocument> AnalyzerConfigFiles,
        IReadOnlyList<string> DefineConstants,
        string LanguageVersion,
        string OutputKind,
        string Nullable,
        bool AllowUnsafe,
        IReadOnlyDictionary<string, string> SpecificDiagnosticOptions,
        string? OutputPath,
        IReadOnlyList<BlueprintProjectReference> ProjectReferences,
        string? TargetFramework,
        bool IsTestProject,
        TestFramework TestFramework,
        string? OutputRefPath = null,
        string GeneralDiagnosticOption = "Default",
        int WarningLevel = 4,
        bool CheckOverflow = false,
        bool Deterministic = true,
        string Platform = "AnyCpu",
        string OptimizationLevel = "Debug",
        string MetadataImportOptions = "Public",
        string DocumentationMode = "Parse",
        IReadOnlyDictionary<string, string>? Features = null,
        IReadOnlyList<string>? Usings = null,
        string? ModuleName = null,
        string? MainTypeName = null,
        string? ScriptClassName = null,
        string? CryptoKeyFile = null,
        string? CryptoKeyContainer = null,
        bool? DelaySign = null,
        string? CryptoPublicKey = null,
        string? StrongNameProvider = null);

    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(CompilationBlueprint))]
    private sealed partial class BlueprintJson : JsonSerializerContext
    {
    }
}
