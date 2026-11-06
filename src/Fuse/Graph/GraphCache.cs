using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fuse.Paths;

namespace Fuse.Graph;

/// <summary>
///     The stored project graph: the evaluated projects kept under the shared directory, which is keyed by the
///     git common directory, so every worktree and every engine on the same inputs reads the graph another engine
///     evaluated instead of evaluating again. The key covers the project files, and the stored form covers every
///     evaluation input (each project file and the imports inside the repository), so a changed project file or
///     import misses and evaluates again. A missing, unreadable or stale stored graph is no graph at all: the
///     caller evaluates, which always answers as the stored form would.
/// </summary>
internal static partial class GraphCache
{
    /// <summary>The stored form's version. A stored graph of another version is left unread.</summary>
    private const int FormatVersion = 1;

    /// <summary>The files MSBuild imports without a project naming them, whose appearing or disappearing changes an evaluation as an import does.</summary>
    private static readonly string[] AutoImportNames = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json"];

    /// <summary>The directory the stored graphs of every worktree of one repository share, under the shared directory.</summary>
    public static string DirectoryFor(string sharedStateDirectory) => LocalState.GraphDirectory(sharedStateDirectory);

    /// <summary>
    ///     The graph stored for <paramref name="paths"/> in <paramref name="root"/>, or null when none is stored,
    ///     when a project file or an import has changed since it was stored, or when what is stored does not read.
    ///     It evaluates nothing and loads no project.
    /// </summary>
    /// <param name="root">The repository the graph belongs to.</param>
    /// <param name="paths">The project files git knows about, as <see cref="RepoGraph.EvaluateAsync"/> lists them.</param>
    public static RepoGraph? TryLoad(RepoRoot root, IReadOnlyList<RepoPath> paths)
    {
        try
        {
            if (KeyFor(root, paths) is not { } key)
                return null;
            var file = FileFor(root.SharedStateDirectory, key);
            if (!File.Exists(file))
                return null;
            if (JsonSerializer.Deserialize(ReadText(file), GraphJson.Default.StoredGraph) is not { } stored)
                return null;
            if (stored.Version != FormatVersion || stored.Projects is null || stored.Inputs is null)
                return null;
            if (!string.Equals(stored.Build, Build, StringComparison.Ordinal))
                return null;
            // A project file git names that the stored graph does not, or one it names that git no longer does,
            // is another graph.
            var storedPaths = stored.Projects.Select(p => p.Path).Order(StringComparer.Ordinal).ToList();
            var currentPaths = paths.Select(p => p.Relative).Order(StringComparer.Ordinal).ToList();
            if (!storedPaths.SequenceEqual(currentPaths, StringComparer.Ordinal))
                return null;
            // A file MSBuild imports without being named, created or deleted since the graph was stored, is
            // another evaluation even though no project file changed.
            var storedAuto = stored.Inputs.Keys.Where(k => AutoImportNames.Contains(Path.GetFileName(k))).Order(StringComparer.Ordinal).ToList();
            if (!storedAuto.SequenceEqual(AutoImports(root), StringComparer.Ordinal))
                return null;
            foreach (var (relative, hash) in stored.Inputs)
            {
                var absolute = root.PathOf(relative).Absolute;
                if (!File.Exists(absolute) || !string.Equals(HashFile(absolute), hash, StringComparison.Ordinal))
                    return null;
            }

            var projects = new List<ProjectNode>(stored.Projects.Count);
            foreach (var project in stored.Projects)
            {
                if (!Enum.TryParse<TestFramework>(project.Framework, out var framework))
                    return null;
                projects.Add(new ProjectNode
                {
                    Path = root.PathOf(project.Path),
                    Name = project.Name,
                    Directory = root.PathOf(project.Directory),
                    References = [.. project.References.Select(root.PathOf)],
                    Sources = new HashSet<RepoPath>(project.Sources.Select(root.PathOf)),
                    NonSources = new HashSet<RepoPath>(project.NonSources.Select(root.PathOf)),
                    BuildOnlyReferences = [.. project.BuildOnlyReferences.Select(root.PathOf)],
                    EvaluationInputs = new HashSet<RepoPath>(project.EvaluationInputs.Select(root.PathOf)),
                    AssetsFile = root.PathOf(project.AssetsFile),
                    IsTest = project.IsTest,
                    UsesTestingPlatform = project.UsesTestingPlatform,
                    Framework = framework,
                    RequiresContainerRuntime = project.RequiresContainerRuntime,
                    TargetPaths = [.. project.TargetPaths],
                    RunSettings = project.RunSettings,
                    IsExecutable = project.IsExecutable,
                });
            }

            return RepoGraph.FromStored(projects, []);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException or NullReferenceException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Stores <paramref name="graph"/> for <paramref name="root"/> under the key its project files give it, so
    ///     the next engine reads it instead of evaluating. A graph with a project that could not be evaluated is
    ///     not stored, and a write that fails is dropped: the next engine evaluates again.
    /// </summary>
    /// <param name="root">The repository the graph belongs to.</param>
    /// <param name="graph">The evaluated graph to store.</param>
    public static void Store(RepoRoot root, RepoGraph graph)
    {
        if (graph.Failures.Count > 0)
            return;
        try
        {
            var paths = graph.Projects.Select(p => p.Path).ToList();
            if (KeyFor(root, paths) is not { } key)
                return;
            var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var project in graph.Projects)
            {
                foreach (var input in project.EvaluationInputs)
                {
                    if (inputs.ContainsKey(input.Relative))
                        continue;
                    if (!File.Exists(input.Absolute))
                        return;
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

            var stored = new StoredGraph(
                FormatVersion,
                Build,
                [.. graph.Projects.OrderBy(p => p.Path.Relative, StringComparer.Ordinal).Select(ToStored)],
                inputs,
                []);
            var file = FileFor(root.SharedStateDirectory, key);
            Write(file, JsonSerializer.Serialize(stored, GraphJson.Default.StoredGraph));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // The stored graph only saves an evaluation; losing the write costs the next engine that evaluation.
        }
    }

    /// <summary>Deletes every stored key but the two most recently written, so the stored graphs stay bounded with no setting.</summary>
    /// <param name="sharedStateDirectory">The shared directory the stored graphs are kept under, as <see cref="DirectoryFor"/> names it for one repository.</param>
    public static void Prune(string sharedStateDirectory)
    {
        try
        {
            var directory = DirectoryFor(sharedStateDirectory);
            if (!System.IO.Directory.Exists(directory))
                return;
            foreach (var key in System.IO.Directory.EnumerateDirectories(directory).Select(d => (Path: d, Written: LatestWrite(d))).OrderByDescending(k => k.Written).Skip(2).ToList())
            {
                try
                {
                    System.IO.Directory.Delete(key.Path, recursive: true);
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

    /// <summary>The build the stored form names: the Fuse build that evaluated it. An engine of another build evaluates again.</summary>
    private static string Build => typeof(GraphCache).Assembly.GetName().Version?.ToString() ?? "";

    private static StoredProject ToStored(ProjectNode project) => new(
        project.Path.Relative,
        project.Name,
        project.Directory.Relative,
        [.. project.References.Select(r => r.Relative).Order(StringComparer.Ordinal)],
        [.. project.Sources.Select(s => s.Relative).Order(StringComparer.Ordinal)],
        [.. project.NonSources.Select(s => s.Relative).Order(StringComparer.Ordinal)],
        [.. project.BuildOnlyReferences.Select(r => r.Relative).Order(StringComparer.Ordinal)],
        [.. project.EvaluationInputs.Select(i => i.Relative).Order(StringComparer.Ordinal)],
        project.AssetsFile.Relative,
        project.IsTest,
        project.UsesTestingPlatform,
        project.Framework.ToString(),
        project.RequiresContainerRuntime,
        [.. project.TargetPaths],
        project.RunSettings,
        project.IsExecutable);

    /// <summary>
    ///     The key the graph of <paramref name="paths"/> is stored under: the project files and the build, hashed.
    ///     Null when a project file cannot be read, and nothing is stored or read then.
    /// </summary>
    private static string? KeyFor(RepoRoot root, IReadOnlyList<RepoPath> paths)
    {
        try
        {
            var lines = new List<string> { FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), Build };
            foreach (var path in paths.OrderBy(p => p.Relative, StringComparer.Ordinal))
            {
                if (!File.Exists(path.Absolute))
                    return null;
                lines.Add("project:" + path.Relative + "=" + HashFile(path.Absolute));
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Every file under the root MSBuild imports without a project naming it, by repository-relative path, outside build output.</summary>
    private static List<string> AutoImports(RepoRoot root)
    {
        var result = new List<string>();
        foreach (var name in AutoImportNames)
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(root.Path, name, SearchOption.AllDirectories))
            {
                if (!PathRules.IsBuildOutput(root.Path, file))
                    result.Add(root.PathOf(file).Relative);
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static string FileFor(string sharedStateDirectory, string key) =>
        Path.Combine(DirectoryFor(sharedStateDirectory), key, "graph.json");

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(SharedRead.AllBytes(path)));

    /// <summary>
    ///     The text <paramref name="file"/> holds, opened for shared reading, so a write from another worktree's
    ///     engine that replaces the file does not fail the read, and the read does not fail that write.
    /// </summary>
    private static string ReadText(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    ///     Writes <paramref name="text"/> to <paramref name="file"/> through a staging file and the key directory's
    ///     write lock, as the baseline store and the summary cache write, so engines of every worktree share the
    ///     stored graph without tearing a file another engine is reading.
    /// </summary>
    private static void Write(string file, string text)
    {
        var staging = file + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        try
        {
            var directory = Path.GetDirectoryName(file)!;
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(staging, text);
            using var locked = AcquireWriteLock(directory);
            if (locked is null)
            {
                TryDelete(staging);
                return;
            }

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(staging, file, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(staging);
        }
    }

    /// <summary>How many times a write waits for another engine's write lock before it drops the write.</summary>
    private const int WriteLockAttempts = 20;

    /// <summary>How long a write waits between write lock attempts.</summary>
    private const int WriteLockRetryMs = 50;

    /// <summary>
    ///     Takes the write lock of <paramref name="directory"/>, waiting while another engine holds it, or null when
    ///     it stays held past the wait. The operating system owns the lock, so an engine that is killed holding it
    ///     releases it at once.
    /// </summary>
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

    /// <summary>When the key folder was last written to, by its files or itself; a folder that cannot be read counts as the oldest.</summary>
    private static DateTime LatestWrite(string directory)
    {
        try
        {
            var latest = System.IO.Directory.GetLastWriteTimeUtc(directory);
            foreach (var file in System.IO.Directory.EnumerateFiles(directory))
            {
                var written = File.GetLastWriteTimeUtc(file);
                if (written > latest)
                    latest = written;
            }

            return latest;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>One stored project: every field of <see cref="ProjectNode"/>, by repository-relative path.</summary>
    private sealed record StoredProject(string Path, string Name, string Directory, List<string> References, List<string> Sources, List<string> NonSources, List<string> BuildOnlyReferences, List<string> EvaluationInputs, string AssetsFile, bool IsTest, bool UsesTestingPlatform, string Framework, bool RequiresContainerRuntime, List<string> TargetPaths, string? RunSettings, bool IsExecutable);

    /// <summary>One stored graph: its projects, and the hash of every evaluation input it was evaluated from.</summary>
    private sealed record StoredGraph(int Version, string Build, List<StoredProject>? Projects, Dictionary<string, string>? Inputs, List<string>? Failures);

    [JsonSerializable(typeof(StoredGraph))]
    private sealed partial class GraphJson : JsonSerializerContext
    {
    }
}
