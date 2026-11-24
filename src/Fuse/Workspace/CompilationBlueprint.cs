using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Fuse.Graph;
using Fuse.Paths;

namespace Fuse.Workspace;

/// <summary>
///     Serializable compilation inputs for a repository's projects, extracted from MSBuild once per project-file
///     state and stored under <c>SharedStateDirectory/blueprints/&lt;key&gt;.json</c>. A resident engine loads projects
///     from the blueprint into a lean <c>AdhocWorkspace</c> instead of holding an <c>MSBuildWorkspace</c> in memory,
///     and an unrestored secondary worktree with matching project/restore inputs maps its sources onto a restored
///     worktree's blueprint without running <c>dotnet restore</c>.
/// </summary>
internal sealed partial class CompilationBlueprint
{
    internal const int FormatVersion = 1;

    private const string RootTokenNative = "${FUSE_REPO_ROOT}";
    private const string RootTokenForward = "${FUSE_REPO_ROOT_FWD}";

    private static readonly string[] AutoImportNames =
        ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "packages.lock.json", "global.json"];

    private const int WriteLockAttempts = 20;
    private const int WriteLockRetryMs = 50;

    public CompilationBlueprint(
        int version,
        string build,
        string key,
        string sourceRoot,
        Dictionary<string, string> inputHashes,
        Dictionary<string, string> assetsHashes,
        List<BlueprintProject> projects)
    {
        Version = version;
        Build = build;
        Key = key;
        SourceRoot = sourceRoot;
        InputHashes = inputHashes;
        AssetsHashes = assetsHashes;
        Projects = projects;
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

            foreach (var relative in AutoImports(root))
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
            if (!File.Exists(filePath))
                return null;
            var json = ReadText(filePath);
            var blueprint = JsonSerializer.Deserialize(json, BlueprintJson.Default.CompilationBlueprint);
            if (blueprint is null || blueprint.Version != FormatVersion || !string.Equals(blueprint.Build, CurrentBuild, StringComparison.Ordinal))
                return null;
            if (blueprint.Projects is null || blueprint.InputHashes is null || blueprint.AssetsHashes is null || string.IsNullOrEmpty(blueprint.SourceRoot))
                return null;
            return blueprint;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
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

    /// <summary>Returns a new blueprint merging <paramref name="updatedProjects"/> and <paramref name="updatedAssets"/> into this blueprint.</summary>
    public CompilationBlueprint Merge(string sourceRoot, Dictionary<string, string> inputHashes, Dictionary<string, string> updatedAssets, IReadOnlyList<BlueprintProject> updatedProjects)
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

        return new CompilationBlueprint(FormatVersion, CurrentBuild, Key, sourceRoot, inputHashes, mergedAssets, mergedProjects);
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
            if (JsonNode.Parse(ReadText(path)) is not { } assets)
                return HashFile(path);
            MaskWorktreePaths(assets, root);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(assets.ToJsonString())));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            return null;
        }
    }

    private static List<string> AutoImports(RepoRoot root)
    {
        var result = new List<string>();
        foreach (var name in AutoImportNames)
        {
            foreach (var file in Directory.EnumerateFiles(root.Path, name, SearchOption.AllDirectories))
            {
                if (!PathRules.IsBuildOutput(root.Path, file))
                    result.Add(root.PathOf(file).Relative);
            }
        }

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
        IReadOnlyList<string>? Usings = null);

    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(CompilationBlueprint))]
    private sealed partial class BlueprintJson : JsonSerializerContext
    {
    }
}
