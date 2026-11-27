using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Fuse.Graph;
using Fuse.Paths;
namespace Fuse.Workspace.Blueprints;

/// <summary>
///     File, hash and lock helpers for blueprint persistence. It reads blueprint files, hashes project inputs
///     and restore outputs, and serializes writers through a directory lock, so the blueprint keeps only its
///     cache checks and merge rules.
/// </summary>
internal static class BlueprintFiles
{
    private static readonly string[] AutoImportNames =
        ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "packages.lock.json", "global.json"];

    private const int WriteLockAttempts = 20;
    private const int WriteLockRetryMs = 50;

    /// <summary>
    ///     How old a file's write time must be before a cached stamp is trusted. A file written within this window
    ///     is read again, so a rewrite the timestamp cannot tell apart from the cached read never answers stale.
    /// </summary>
    private static readonly TimeSpan SettledWindow = TimeSpan.FromSeconds(2);

    /// <summary>True when <paramref name="writtenUtc"/> is old enough that no live writer can still hold the file.</summary>
    internal static bool IsSettled(DateTime writtenUtc) => DateTime.UtcNow - writtenUtc >= SettledWindow;

    /// <summary>
    ///     Every MSBuild and NuGet auto-import of <paramref name="graph"/>'s projects: the well-known file names in
    ///     each project's directory and its ancestors up to the root, which is where MSBuild and NuGet look them up.
    ///     A same-named file with no project beneath it is never imported, so it stays out of the key.
    /// </summary>
    internal static List<string> AutoImports(RepoRoot root, RepoGraph graph)
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

    /// <summary>Computes the hex SHA-256 of the file at <paramref name="path"/>.</summary>
    internal static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(SharedRead.AllBytes(path)));

    /// <summary>Reads the file at <paramref name="file"/> with shared read access, so a live writer never blocks the read.</summary>
    internal static string ReadText(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Masks every worktree path inside <paramref name="node"/> so the hash reads the same from any worktree.</summary>
    internal static void MaskWorktreePaths(JsonNode node, RepoRoot root)
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

    /// <summary>Returns the repo-relative form of <paramref name="value"/> when it lies under <paramref name="root"/>, or null.</summary>
    internal static string? MaskedPath(string value, RepoRoot root) =>
        value.Length > root.Path.Length && root.Contains(value) ? root.PathOf(value).Relative : null;

    /// <summary>Takes the directory write lock for blueprint writes, or null when it stays held.</summary>
    internal static FileStream? AcquireWriteLock(string directory)
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

    /// <summary>Deletes <paramref name="file"/> when it exists, ignoring a lock or permission failure.</summary>
    internal static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
