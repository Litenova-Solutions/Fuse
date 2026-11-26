using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Testing.Model;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;

namespace Fuse.Testing;

/// <summary>
///     Content-addressed per-slice test cache persisted under <c>SharedStateDirectory/test-cache/</c>. Each entry is
///     keyed by the deterministic SHA-256 of the test project emit hash, dependency closure hashes, non-source asset
///     hashes, runsettings and referenced-settings hashes, the files under the directory the test runs from, and the
///     filter hash, so edits in disjoint slices hit cached results in ~0 ms. An input that cannot be read declines
///     caching instead of reusing a stored pass.
/// </summary>
internal sealed partial class SliceTestCache
{
    private const int FormatVersion = 2;
    internal const int MaxMemoryEntries = 512;
    internal const int MaxFileHashEntries = 2048;
    internal const int MaxDiskEntries = 2048;

    private static readonly Lock _liveLock = new();
    private static readonly List<WeakReference<SliceTestCache>> _live = [];

    private readonly string _directory;
    private readonly ConcurrentDictionary<string, SliceCacheResult> _memory = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (long Length, DateTime WrittenUtc, string Hash)> _fileHashes = new(StringComparer.OrdinalIgnoreCase);

    public SliceTestCache(string sharedStateDirectory)
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

            _live.Add(new WeakReference<SliceTestCache>(this));
        }
    }

    /// <summary>Directory where slice test cache files are stored for <paramref name="sharedStateDirectory"/>.</summary>
    public static string DirectoryFor(string sharedStateDirectory) => Path.Combine(sharedStateDirectory, "test-cache");

    /// <summary>Inputs that uniquely identify one test project slice execution.</summary>
    internal sealed record KeyInputs(
        string TestProjectRelativePath,
        string TargetFramework,
        string TestProjectEmitHash,
        IReadOnlyList<(string Key, string Hash)> ClosureDependencyHashes,
        IReadOnlyList<(string RelativePath, string Hash)> NonSourceHashes,
        string RunSettingsHash,
        string FilterHash,
        IReadOnlyList<(string RelativePath, string Hash)>? OutputHashes = null,
        string SettingsFileHash = "");

    /// <summary>Computes the deterministic SHA-256 key from normalized slice inputs.</summary>
    public static string ComputeKey(KeyInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var builder = new StringBuilder();
        AppendField(builder, $"v{FormatVersion}");
        AppendField(builder, inputs.TestProjectRelativePath);
        AppendField(builder, inputs.TargetFramework);
        AppendField(builder, inputs.TestProjectEmitHash);

        foreach (var (key, hash) in inputs.ClosureDependencyHashes.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            AppendField(builder, key);
            AppendField(builder, hash);
        }

        AppendField(builder, "|");
        foreach (var (path, hash) in inputs.NonSourceHashes.OrderBy(n => n.RelativePath, StringComparer.Ordinal))
        {
            AppendField(builder, path);
            AppendField(builder, hash);
        }

        AppendField(builder, "|");
        if (inputs.OutputHashes is not null)
        {
            foreach (var (path, hash) in inputs.OutputHashes.OrderBy(n => n.RelativePath, StringComparer.Ordinal))
            {
                AppendField(builder, path);
                AppendField(builder, hash);
            }
        }

        AppendField(builder, "|");
        AppendField(builder, inputs.RunSettingsHash);
        AppendField(builder, inputs.SettingsFileHash);
        AppendField(builder, inputs.FilterHash);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>Computes a canonical SHA-256 hash of a run filter and platform filter arguments.</summary>
    public static string HashFilter(string? filter, IReadOnlyList<string> filterArguments)
    {
        var builder = new StringBuilder();
        AppendField(builder, filter ?? "");
        foreach (var argument in filterArguments)
            AppendField(builder, argument);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>
    ///     Computes the deterministic SHA-256 slice key for <paramref name="testProject"/> using emitted PE/PDB hashes
    ///     when present, falling back to shadow or build-output files on disk, plus all dependency, non-source,
    ///     runsettings and referenced-settings hashes across the project's closure (including build-only references),
    ///     and the content of every file under the directory the test runs from. Null when any input cannot be read,
    ///     so a missed input never reuses a stored pass.
    /// </summary>
    public string? ComputeKey(
        Project testProject,
        RepoGraph graph,
        RepoRoot root,
        IReadOnlyList<Project> closure,
        IReadOnlyDictionary<ProjectId, (EmittedImage Image, string PeHash, string PdbHash)> emitted,
        string? shadowDirectory,
        string? runSettingsPath,
        string? filter,
        IReadOnlyList<string> filterArguments)
    {
        if (testProject.FilePath is null)
            return null;
        var testNode = graph.Find(root.PathOf(testProject.FilePath));
        if (testNode is null)
            return null;

        var testTfm = TargetFrameworkOf(testProject);
        var testHash = AssemblyHash(testProject, emitted, shadowDirectory);
        if (testHash is null)
            return null;

        var depHashes = new List<(string Key, string Hash)>();
        var seenProjects = new HashSet<RepoPath>();
        var seenNodes = new List<ProjectNode>();
        var closureNodes = new Stack<ProjectNode>();
        foreach (var project in closure)
        {
            if (project.FilePath is not null && graph.Find(root.PathOf(project.FilePath)) is { } node && seenProjects.Add(node.Path))
            {
                seenNodes.Add(node);
                closureNodes.Push(node);
            }
            if (project.Id == testProject.Id)
                continue;
            var depHash = AssemblyHash(project, emitted, shadowDirectory);
            if (depHash is null)
                return null;
            var depRelative = project.FilePath is null ? project.Name : root.PathOf(project.FilePath).Relative;
            depHashes.Add(($"{depRelative}|{TargetFrameworkOf(project)}", depHash));
        }

        // Include transitive build-only references (for example an application host launched as a child process).
        while (closureNodes.Count > 0)
        {
            var current = closureNodes.Pop();
            foreach (var buildOnly in current.BuildOnlyReferences)
            {
                if (graph.Find(buildOnly) is not { } target || !seenProjects.Add(target.Path))
                    continue;
                seenNodes.Add(target);
                closureNodes.Push(target);
                var roslynTargets = RepoWorkspace.ProjectsFor(testProject.Solution, target).ToList();
                if (roslynTargets.Count > 0)
                {
                    foreach (var roslynTarget in roslynTargets)
                    {
                        var h = AssemblyHash(roslynTarget, emitted, null);
                        if (h is null)
                            return null;
                        depHashes.Add(($"buildonly:{target.Path.Relative}|{TargetFrameworkOf(roslynTarget)}", h));
                    }
                }
                else
                {
                    foreach (var targetPath in target.TargetPaths)
                    {
                        var h = HashAssemblyOnDisk(targetPath);
                        if (h is null)
                            return null;
                        depHashes.Add(($"buildonly:{target.Path.Relative}|{Path.GetFileName(Path.GetDirectoryName(targetPath))}", h));
                    }
                }
            }

            foreach (var reference in current.References)
            {
                if (graph.Find(reference) is { } refNode && seenProjects.Add(refNode.Path))
                {
                    seenNodes.Add(refNode);
                    closureNodes.Push(refNode);
                }
            }
        }

        var nonSourceHashes = new List<(string RelativePath, string Hash)>();
        var seenNonSources = new HashSet<RepoPath>();
        foreach (var node in seenNodes)
        {
            foreach (var nonSource in node.NonSources.Concat(node.EvaluationInputs))
            {
                if (!seenNonSources.Add(nonSource))
                    continue;
                var hash = HashFile(nonSource.Absolute);
                if (hash is null)
                    return null;
                nonSourceHashes.Add((nonSource.Relative, hash));
            }
        }

        string runSettingsHash = "";
        if (runSettingsPath is not null)
        {
            var settingsHash = HashFile(runSettingsPath);
            if (settingsHash is null)
                return null;
            runSettingsHash = settingsHash;
        }

        var referencedSettingsHash = ReferencedSettingsHash(runSettingsPath);
        if (referencedSettingsHash is null)
            return null;
        var outputHashes = OutputFileHashes(shadowDirectory, testProject);
        if (outputHashes is null)
            return null;
        var filterHash = HashFilter(filter, filterArguments);

        return ComputeKey(new KeyInputs(
            testNode.Path.Relative,
            testTfm,
            testHash,
            depHashes,
            nonSourceHashes,
            runSettingsHash,
            filterHash,
            outputHashes,
            referencedSettingsHash));
    }

    /// <summary>Returns the cached test result for <paramref name="sliceKey"/>, or null on miss or when <paramref name="noCache"/> is true.</summary>
    public SliceCacheResult? TryGet(string? sliceKey, bool noCache = false)
    {
        if (noCache || string.IsNullOrWhiteSpace(sliceKey))
            return null;

        if (_memory.TryGetValue(sliceKey, out var cached))
            return cached;

        var path = PathFor(sliceKey);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            var stored = JsonSerializer.Deserialize(json, SliceCacheJson.Default.StoredSliceResult);
            if (stored is null || stored.Version != FormatVersion || !string.Equals(stored.SliceKey, sliceKey, StringComparison.Ordinal))
                return null;
            var result = ToModel(stored);
            RememberInMemory(result);
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Persists <paramref name="result"/> atomically under <c>SharedStateDirectory/test-cache/</c>.</summary>
    public void Store(SliceCacheResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(result.SliceKey))
            return;

        RememberInMemory(result);
        try
        {
            Directory.CreateDirectory(_directory);
            var path = PathFor(result.SliceKey);
            var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            var payload = new StoredSliceResult(
                FormatVersion,
                result.SliceKey,
                result.ProjectPath,
                result.ProjectName,
                result.TargetFramework,
                result.Passed,
                result.Failed,
                result.Skipped,
                result.DurationMs,
                result.CachedAtUtc,
                [.. result.Failures.Select(f => new StoredSliceFailure(f.Name, f.Message, [.. f.Frames]))]);
            File.WriteAllText(temp, JsonSerializer.Serialize(payload, SliceCacheJson.Default.StoredSliceResult));
            File.Move(temp, path, overwrite: true);
            PruneDiskIfNeeded();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Clears in-memory and on-disk cached slice results.</summary>
    public void Clear()
    {
        _memory.Clear();
        _fileHashes.Clear();
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    ///     Drops this cache's in-memory results and file hashes, keeping what is on disk, so an idle engine
    ///     returns that memory; the next lookup reads the stored result again or hashes the file again.
    /// </summary>
    /// <returns>How many in-memory entries were dropped.</returns>
    public int ReleaseIdle()
    {
        var dropped = _memory.Count + _fileHashes.Count;
        _memory.Clear();
        _fileHashes.Clear();
        return dropped;
    }

    /// <summary>
    ///     Releases every live slice cache's in-memory entries, keeping what is on disk, so an idle engine
    ///     returns that memory.
    /// </summary>
    /// <returns>How many in-memory entries were dropped across every live cache.</returns>
    public static int ReleaseIdleAll()
    {
        List<SliceTestCache> targets;
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

    private string PathFor(string sliceKey) => Path.Combine(_directory, sliceKey + ".json");

    private string? AssemblyHash(
        Project project,
        IReadOnlyDictionary<ProjectId, (EmittedImage Image, string PeHash, string PdbHash)> emitted,
        string? shadowDirectory)
    {
        if (emitted.TryGetValue(project.Id, out var image))
            return image.PeHash + ":" + image.PdbHash;
        if (project.OutputFilePath is null)
            return null;

        // If a shadow directory holds a kept emitted assembly (unchanged dependent across two edits), prefer it.
        if (shadowDirectory is not null)
        {
            var shadowAssembly = Path.Combine(shadowDirectory, Path.GetFileName(project.OutputFilePath));
            if (File.Exists(shadowAssembly))
                return HashAssemblyOnDisk(shadowAssembly);
        }

        return HashAssemblyOnDisk(project.OutputFilePath);
    }

    private string? HashAssemblyOnDisk(string assemblyPath)
    {
        var peHash = HashFile(assemblyPath);
        if (peHash is null)
            return null;
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (!File.Exists(pdbPath))
            return peHash + ":no-pdb";
        var pdbHash = HashFile(pdbPath);
        if (pdbHash is null)
            return null;
        return peHash + ":" + pdbHash;
    }

    /// <summary>
    ///     Content hashes of every file under the directory the test runs from, by path relative to it, so binary-only
    ///     inputs, native libraries and generated files are key inputs too. The shadow copy when one was prepared, else
    ///     the test project's build output. Null when the directory cannot be listed or any file cannot be read, which
    ///     declines caching. The plan's lock files and leftover temporary files are not inputs.
    /// </summary>
    private List<(string RelativePath, string Hash)>? OutputFileHashes(string? shadowDirectory, Project testProject)
    {
        var directory = shadowDirectory;
        if (directory is null && testProject.OutputFilePath is { } output)
            directory = Path.GetDirectoryName(output);
        if (directory is null)
            return null;
        try
        {
            var hashes = new List<(string RelativePath, string Hash)>();
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith(".in-use-", StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    continue;
                var hash = HashFile(file);
                if (hash is null)
                    return null;
                hashes.Add((Path.GetRelativePath(directory, file), hash));
            }

            return hashes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Hash of the settings file the runsettings at <paramref name="runSettingsPath"/> names for MSTest, whose content
    ///     changes results without changing the runsettings text. Empty when none is named; null when the named file cannot
    ///     be read, which declines caching. A runsettings file that is not XML names nothing.
    /// </summary>
    private string? ReferencedSettingsHash(string? runSettingsPath)
    {
        if (runSettingsPath is null)
            return "";
        string? referenced;
        try
        {
            referenced = XDocument.Load(runSettingsPath).Root?.Element("MSTest")?.Element("SettingsFile")?.Value.Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return "";
        }

        if (string.IsNullOrEmpty(referenced))
            return "";
        var absolute = Path.IsPathRooted(referenced)
            ? referenced
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(runSettingsPath)!, referenced));
        return HashFile(absolute);
    }

    private string? HashFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;
            if (_fileHashes.TryGetValue(path, out var cached)
                && cached.Length == info.Length
                && cached.WrittenUtc == info.LastWriteTimeUtc)
                return cached.Hash;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (_fileHashes.Count >= MaxFileHashEntries)
                _fileHashes.Clear();
            _fileHashes[path] = (info.Length, info.LastWriteTimeUtc, hash);
            return hash;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void RememberInMemory(SliceCacheResult result)
    {
        if (_memory.Count >= MaxMemoryEntries && !_memory.ContainsKey(result.SliceKey))
            _memory.Clear();
        _memory[result.SliceKey] = result;
    }

    private void PruneDiskIfNeeded()
    {
        try
        {
            var files = Directory.GetFiles(_directory, "*.json");
            if (files.Length <= MaxDiskEntries)
                return;
            foreach (var victim in files
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.LastWriteTimeUtc)
                .Take(files.Length - MaxDiskEntries))
            {
                try
                {
                    victim.Delete();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string TargetFrameworkOf(Project project)
    {
        if (project.OutputFilePath is { } output)
        {
            var dir = Path.GetFileName(Path.GetDirectoryName(output));
            if (!string.IsNullOrEmpty(dir))
                return dir;
        }

        var open = project.Name.LastIndexOf('(');
        var close = project.Name.LastIndexOf(')');
        return open >= 0 && close > open ? project.Name[(open + 1)..close] : "";
    }

    private static void AppendField(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');

    private static SliceCacheResult ToModel(StoredSliceResult stored) => new(
        stored.SliceKey,
        stored.ProjectPath,
        stored.ProjectName,
        stored.TargetFramework,
        stored.Passed,
        stored.Failed,
        stored.Skipped,
        stored.DurationMs,
        stored.CachedAtUtc,
        [.. stored.Failures.Select(f => new SliceTestFailure(f.Name, f.Message, [.. f.Frames]))]);

    private sealed record StoredSliceResult(
        int Version,
        string SliceKey,
        string ProjectPath,
        string ProjectName,
        string TargetFramework,
        int Passed,
        int Failed,
        int Skipped,
        double DurationMs,
        DateTime CachedAtUtc,
        StoredSliceFailure[] Failures);

    private sealed record StoredSliceFailure(string Name, string Message, string[] Frames);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(StoredSliceResult))]
    private sealed partial class SliceCacheJson : JsonSerializerContext;
}
