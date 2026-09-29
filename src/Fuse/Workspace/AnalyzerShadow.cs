using System.Security.Cryptography;
using System.Text;
using Fuse.Paths;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Workspace;

/// <summary>
///     Loads a repository's own analyzers and source generators from a copy in the state directory. Roslyn keeps an
///     analyzer assembly open for as long as it is loaded, and the engine lives for 30 idle minutes, so an analyzer the
///     repository builds itself (a project referenced with <c>OutputItemType="Analyzer"</c>) would otherwise stay locked
///     under its <c>bin</c> folder, and the next real build of that project would fail to copy its output with MSB3021.
/// </summary>
/// <remarks>
///     Only analyzers inside the repository are copied: package analyzers live in the NuGet cache and SDK analyzers in
///     the SDK, which no build writes. A copy holds the analyzer's whole directory, so its dependencies resolve next to it
///     as they do from the original, and a rebuilt analyzer gets a new copy, because the loader never unloads the old one.
/// </remarks>
internal sealed class AnalyzerShadow
{
    private readonly RepoRoot _root;
    private readonly string _directory;
    private readonly Dictionary<RepoPath, (string Stamp, AnalyzerFileReference Reference)> _references = [];
    private Solution? _lastInput;
    private Solution? _lastOutput;

    public AnalyzerShadow(RepoRoot root)
    {
        _root = root;
        _directory = Path.Combine(root.StateDirectory, "analyzers");
        // Copies left by an earlier engine are no longer loaded by anything; a copy still in use is skipped.
        try
        {
            if (Directory.Exists(_directory))
            {
                foreach (var old in Directory.EnumerateDirectories(_directory))
                    TryDelete(old);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    ///     Returns <paramref name="solution"/> with every in-repository analyzer reference pointing at its copy. The same
    ///     loader solution gets the same result object back, so the compilations Roslyn caches on it survive the next
    ///     rebuild of the views; a new result would drop them and recompile every project from scratch.
    /// </summary>
    public Solution Apply(Solution solution)
    {
        if (ReferenceEquals(solution, _lastInput) && _lastOutput is not null)
            return _lastOutput;

        var result = solution;
        foreach (var id in solution.ProjectIds)
        {
            var references = result.GetProject(id)!.AnalyzerReferences;
            var shadowed = references.Select(Shadow).ToList();
            if (!shadowed.SequenceEqual(references))
                result = result.WithProjectAnalyzerReferences(id, shadowed);
        }

        _lastInput = solution;
        _lastOutput = result;
        return result;
    }

    private AnalyzerReference Shadow(AnalyzerReference reference)
    {
        // Package and SDK analyzers are spelled outside the root, so the spelling decides without a file system call.
        if (reference is not AnalyzerFileReference file || !_root.Contains(file.FullPath) || !File.Exists(file.FullPath))
            return reference;

        try
        {
            var original = _root.PathOf(file.FullPath);
            var source = Path.GetDirectoryName(file.FullPath)!;
            var stamp = Stamp(source);
            if (_references.TryGetValue(original, out var known) && known.Stamp == stamp)
                return known.Reference;

            var target = Path.Combine(_directory, Hash(source + "|" + stamp));
            if (!Directory.Exists(target))
            {
                var staging = target + ".tmp-" + Environment.ProcessId;
                Directory.CreateDirectory(staging);
                foreach (var dll in Directory.EnumerateFiles(source, "*.dll"))
                    File.Copy(dll, Path.Combine(staging, Path.GetFileName(dll)), overwrite: true);
                Directory.Move(staging, target);
            }

            var copy = Path.Combine(target, Path.GetFileName(file.FullPath));
            file.AssemblyLoader.AddDependencyLocation(copy);
            var shadow = new AnalyzerFileReference(copy, file.AssemblyLoader);
            _references[original] = (stamp, shadow);
            return shadow;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without a copy the analyzer still runs from where it is; only the lock on its file comes back.
            return reference;
        }
    }

    /// <summary>Changes whenever any assembly in the directory is rebuilt, added or removed.</summary>
    private static string Stamp(string directory)
    {
        var text = new StringBuilder();
        foreach (var dll in Directory.EnumerateFiles(directory, "*.dll").Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(dll);
            text.Append(info.Name).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).Append(';');
        }

        return text.ToString();
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
