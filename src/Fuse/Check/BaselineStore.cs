using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Fuse.Check.Model;
using Fuse.Graph;
using Fuse.Paths;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Check;

/// <summary>
///     HEAD's finished baseline results on disk, keyed by what they depend on, so the first check after an engine
///     restart reads them instead of binding HEAD again. Anything unreadable or from another key is ignored and computed
///     again, and unfinished results are never written. The worktrees of one repository share one store, so a worktree
///     reads what another worktree on the same commit wrote.
/// </summary>
internal sealed partial class BaselineStore
{
    private readonly RepoRoot _root;
    private readonly Action<string> _log;
    private bool _writeLogged;

    /// <param name="root">The repository, for the state directory its results are kept in.</param>
    /// <param name="log">Where a write that fails is written, once.</param>
    public BaselineStore(RepoRoot root, Action<string> log)
    {
        _root = root;
        _log = log;
    }

    /// <summary>
    ///     The key the baseline results for <paramref name="head"/> are kept under, or null when nothing is kept for it:
    ///     the repository has no commit, or anything the results depend on cannot be read. The key covers HEAD, the
    ///     Roslyn build, and per loaded project its evaluation inputs, its restore output, its analyzer assemblies, its
    ///     additional files and its analyzer configuration options, plus every editorconfig and globalconfig file, so a
    ///     result is read only when all of those are as they were. Paths inside the repository are named by their
    ///     repository-relative form and restore output is hashed with its worktree paths masked, so worktrees of one
///     repository on the same commit share one key.
    /// </summary>
    public static string? KeyFor(string? head, Solution baseline, RepoGraph graph, RepoRoot root)
    {
        if (head is null)
            return null;
        try
        {
            var lines = new List<string> { "2", head, typeof(Compilation).Assembly.GetName().Version?.ToString() ?? "" };
            foreach (var group in baseline.Projects.Where(p => p.FilePath is not null).GroupBy(p => p.FilePath).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                if (graph.Find(root.PathOf(group.Key!)) is not { } node)
                    return null;
                lines.Add("project:" + node.Path.Relative);
                foreach (var input in node.EvaluationInputs.OrderBy(p => p.Relative, StringComparer.Ordinal))
                    lines.Add("input:" + input.Relative + "=" + HashFile(input.Absolute));
                if (HashAssets(root, node.AssetsFile.Absolute) is not { } assetsHash)
                    return null;
                lines.Add("assets:" + node.AssetsFile.Relative + "=" + assetsHash);
                var references = group.SelectMany(p => p.AnalyzerReferences).ToList();
                if (references.Any(r => r is not AnalyzerFileReference && r is not UnresolvedAnalyzerReference))
                    return null;
                foreach (var analyzer in references.OfType<AnalyzerFileReference>().Select(r => r.FullPath).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
                {
                    if (KeyPathFor(root, analyzer) is not { } analyzerPath)
                        return null;
                    lines.Add("analyzer:" + analyzerPath + "=" + HashFile(analyzer));
                }
                var additionalPaths = new List<string>();
                foreach (var file in group.SelectMany(p => p.AdditionalDocuments).Select(d => d.FilePath))
                {
                    if (string.IsNullOrEmpty(file))
                        return null;
                    additionalPaths.Add(file);
                }

                foreach (var additional in additionalPaths.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
                {
                    if (KeyPathFor(root, additional) is not { } additionalPath)
                        return null;
                    lines.Add("additional:" + additionalPath + "=" + HashFile(additional));
                }
                foreach (var project in group.OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    var options = project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions;
                    foreach (var key in options.Keys.Order(StringComparer.Ordinal))
                    {
                        if (options.TryGetValue(key, out var value))
                            lines.Add("option:" + node.Path.Relative + "|" + project.Name + "|" + key + "=" + value);
                    }
                }
            }

            foreach (var config in ConfigFiles(root))
                lines.Add("config:" + config.Relative + "=" + HashFile(config.Absolute));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Every editorconfig and globalconfig file under the root, which set analyzer severities no project file names.</summary>
    private static List<RepoPath> ConfigFiles(RepoRoot root)
    {
        var result = new List<RepoPath>();
        foreach (var name in new[] { ".editorconfig", ".globalconfig" })
        {
            foreach (var file in Directory.EnumerateFiles(root.Path, name, SearchOption.AllDirectories))
                result.Add(root.PathOf(file));
        }

        result.Sort((a, b) => string.Compare(a.Relative, b.Relative, StringComparison.Ordinal));
        return result;
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>
    ///     How <paramref name="absolute"/> is named in the key: its repository-relative form with forward slashes when
    ///     it lies inside the repository, so worktrees of one repository name it alike, and itself otherwise, such as a
    ///     package analyzer in the NuGet cache, whose absolute path is the same for every worktree. Null when it cannot
    ///     be named, and nothing is kept for the baseline then.
    /// </summary>
    internal static string? KeyPathFor(RepoRoot root, string absolute)
    {
        try
        {
            var relative = root.PathOf(absolute).Relative;
            return relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? absolute : relative;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The hash of the restore output <paramref name="path"/> holds, with every path under the repository replaced
    ///     by its repository-relative form, so worktrees of one repository hash it alike although restore writes their
    ///     own absolute paths into it. Paths outside the repository, such as the package cache, are hashed as they are.
    ///     Null when it cannot be hashed, and nothing is kept for the baseline then.
    /// </summary>
    internal static string? HashAssets(RepoRoot root, string path)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not { } assets)
                return HashFile(path);
            MaskWorktreePaths(assets, root.Path);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(assets.ToJsonString())));
        }
        catch (Exception e) when (e is ArgumentException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Replaces every string under <paramref name="node"/> that names a path under <paramref name="root"/> with its repository-relative form.</summary>
    private static void MaskWorktreePaths(JsonNode node, string root)
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

    /// <summary>The repository-relative form of <paramref name="value"/> when it names a path under <paramref name="root"/>, or null otherwise.</summary>
    private static string? MaskedPath(string value, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (value.Length > root.Length
            && value.StartsWith(root, comparison)
            && (value[root.Length] == Path.DirectorySeparatorChar || value[root.Length] == Path.AltDirectorySeparatorChar))
            return value[(root.Length + 1)..].Replace('\\', '/');
        return null;
    }

    /// <summary>
    ///     What identifies a project's stored result: its project file and the Roslyn project's name, which a target
    ///     framework makes distinct. Null when the project has no file, which is never stored.
    /// </summary>
    public string? ProjectKeyFor(Project project) =>
        project.FilePath is null ? null : _root.PathOf(project.FilePath).Relative + "\n" + project.Name;

    /// <summary>HEAD's stored errors in <paramref name="path"/> under <paramref name="key"/>, or null when none are stored there.</summary>
    /// <returns>The errors, and whether the run that stored them included the compilation-wide analyzers.</returns>
    public (IReadOnlyList<CompilerError> Errors, bool CompilationWide)? TryReadFile(string key, RepoPath path) =>
        Read(Path.Combine(DirectoryFor(key), FileNameFor("f", path.Relative)));

    /// <summary>HEAD's stored errors in the project <paramref name="projectKey"/> names under <paramref name="key"/>, or null when none are stored there.</summary>
    public IReadOnlyList<CompilerError>? TryReadProject(string key, string projectKey)
    {
        if (Read(Path.Combine(DirectoryFor(key), FileNameFor("p", projectKey))) is not { } stored)
            return null;
        return stored.Errors;
    }

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

    private static (IReadOnlyList<CompilerError> Errors, bool CompilationWide)? Read(string file)
    {
        try
        {
            if (!File.Exists(file))
                return null;
            if (ReadText(file) is not { } text)
                return null;
            if (JsonSerializer.Deserialize(text, StoreJson.Default.StoredResult) is not { Errors: not null } stored)
                return null;
            var errors = new List<CompilerError>(stored.Errors.Count);
            foreach (var error in stored.Errors)
            {
                if (error is null || string.IsNullOrEmpty(error.Path) || string.IsNullOrEmpty(error.Id) || error.Message is null || error.Line < 1 || error.Column < 1)
                    return null;
                errors.Add(new CompilerError(error.Path, error.Line, error.Column, error.Id, error.Message, error.FromAnalyzer));
            }

            return (errors, stored.CompilationWide);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Stores <paramref name="errors"/> for <paramref name="path"/> under <paramref name="key"/>. A write that fails is dropped and logged once.</summary>
    /// <param name="compilationWide">Whether the run that found them included the compilation-wide analyzers.</param>
    public void WriteFile(string key, RepoPath path, IReadOnlyList<CompilerError> errors, bool compilationWide) =>
        Write(Path.Combine(DirectoryFor(key), FileNameFor("f", path.Relative)), new StoredResult([.. errors.Select(e => new StoredError(e.Path, e.Line, e.Column, e.Id, e.Message, e.FromAnalyzer))], compilationWide));

    /// <summary>Stores <paramref name="errors"/> for the project <paramref name="projectKey"/> names under <paramref name="key"/>.</summary>
    public void WriteProject(string key, string projectKey, IReadOnlyList<CompilerError> errors) =>
        Write(Path.Combine(DirectoryFor(key), FileNameFor("p", projectKey)), new StoredResult([.. errors.Select(e => new StoredError(e.Path, e.Line, e.Column, e.Id, e.Message, e.FromAnalyzer))], CompilationWide: true));

    private void Write(string file, StoredResult result)
    {
        try
        {
            var directory = Path.GetDirectoryName(file)!;
            Directory.CreateDirectory(directory);
            var staging = file + ".tmp-" + Environment.ProcessId;
            File.WriteAllText(staging, JsonSerializer.Serialize(result, StoreJson.Default.StoredResult));
            // The worktrees of one repository share the store, so a write takes the key's lock while it replaces the
            // file. A write that cannot take the lock is dropped; the baseline is computed again.
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
                catch (IOException)
                {
                    TryDelete(staging);
                    throw;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            if (!_writeLogged)
            {
                _writeLogged = true;
                _log($"baseline store write failed: {e.Message}");
            }
        }
    }

    /// <summary>How many times a write waits for another engine's write lock before it drops the write.</summary>
    private const int WriteLockAttempts = 20;

    /// <summary>How long a write waits between write lock attempts.</summary>
    private const int WriteLockRetryMs = 50;

    /// <summary>
    ///     Takes the write lock of the key <paramref name="directory"/> holds, waiting while another engine holds it,
    ///     or null when it stays held past the wait. The operating system owns the lock, so an engine that is killed
    ///     holding it releases it at once.
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

    /// <summary>Deletes every stored key but the two most recently written, so the store stays bounded with no setting.</summary>
    public static void Prune(string stateDirectory)
    {
        try
        {
            var directory = Path.Combine(stateDirectory, "baseline");
            if (!Directory.Exists(directory))
                return;
            foreach (var key in Directory.EnumerateDirectories(directory).Select(d => (Path: d, Written: LatestWrite(d))).OrderByDescending(k => k.Written).Skip(2).ToList())
            {
                try
                {
                    Directory.Delete(key.Path, recursive: true);
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

    /// <summary>When the key folder was last written to, by its files or itself; a folder that cannot be read counts as the oldest.</summary>
    private static DateTime LatestWrite(string directory)
    {
        try
        {
            var latest = Directory.GetLastWriteTimeUtc(directory);
            foreach (var file in Directory.EnumerateFiles(directory))
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

    private string DirectoryFor(string key) => Path.Combine(_root.SharedStateDirectory, "baseline", key);

    private static string FileNameFor(string prefix, string value) =>
        prefix + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))) + ".json";

    /// <summary>One stored error: every field of <see cref="CompilerError"/>.</summary>
    private sealed record StoredError(string Path, int Line, int Column, string Id, string Message, bool FromAnalyzer);

    /// <summary>One stored result: the errors, and whether the run that found them included the compilation-wide analyzers.</summary>
    private sealed record StoredResult(List<StoredError>? Errors, bool CompilationWide);

    [JsonSerializable(typeof(StoredResult))]
    private sealed partial class StoreJson : JsonSerializerContext
    {
    }
}
