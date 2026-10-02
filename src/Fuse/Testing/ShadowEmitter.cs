using System.Diagnostics;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Testing.Model;
using Microsoft.CodeAnalysis;

namespace Fuse.Testing;

/// <summary>
///     Prepares a shadow run of a test assembly: copies the test project's last build output into a shadow directory and
///     overwrites the assemblies whose sources changed, or whose copy there is not their project's last build, with ones
///     emitted from the warm compilations.
/// </summary>
/// <remarks>
///     <para>
///         A shadow run is prepared only when no project file, import, resource or content file in the involved project
///         directories has a timestamp newer than the build output (freshness is judged by timestamps, not content).
///         Otherwise <see cref="PrepareAsync"/> returns <see cref="RunMode.Build"/> and the client runs <c>dotnet test</c>
///         on the project, which builds it with MSBuild.
///     </para>
///     <para>
///         One emitter serves every plan of the engine. It keeps the emitted images in an <see cref="EmitCache"/>, and
///         remembers each shadow file it wrote, so an image that is already in place is not written again and the copy
///         from build output never overwrites a file it is about to emit. It runs after the request lock is released,
///         against the snapshot the plan took under it; <see cref="TestPlanner"/> prepares one plan at a time.
///     </para>
/// </remarks>
internal sealed class ShadowEmitter
{
    private readonly RepoRoot _root;
    private readonly EmitCache _cache = new();

    /// <summary>Each shadow file this emitter wrote, with the bytes it wrote and the length and time the file had after.</summary>
    private readonly Dictionary<string, Written> _written = new(StringComparer.OrdinalIgnoreCase);

    public ShadowEmitter(RepoRoot root) => _root = root;

    /// <summary>How many assemblies were emitted since the engine started, as opposed to taken from the cache.</summary>
    public int Emits => _cache.Emits;

    /// <summary>Returns a shadow run of the prepared test assembly, or a build with MSBuild when a shadow run is not safe.</summary>
    /// <param name="testProject">The Roslyn project for one target framework of the test project, from the plan's snapshot.</param>
    /// <param name="graph">The project graph the plan's snapshot was taken with.</param>
    /// <param name="log">Receives the reason when a shadow run is refused.</param>
    /// <param name="emitting">Runs while assemblies are emitted and written, so the caller can tell the emit from the rest.</param>
    /// <param name="cancellationToken">Cancels emitting.</param>
    public async Task<RunMode> PrepareAsync(Project testProject, RepoGraph graph, Action<string> log, Stopwatch emitting, CancellationToken cancellationToken)
    {
        var solution = testProject.Solution;
        var testOutput = testProject.OutputFilePath;
        if (testOutput is null || !File.Exists(testOutput))
        {
            log($"{testProject.Name}: no build output yet");
            return new RunMode.Build();
        }

        // Every project the test assembly loads, with the build output it would be copied from.
        var closure = Closure(testProject).ToList();
        var testOutputDirectory = Path.GetDirectoryName(testOutput)!;
        var stale = new HashSet<ProjectId>();
        foreach (var project in closure)
        {
            if (project.OutputFilePath is null || !File.Exists(project.OutputFilePath))
            {
                log($"{project.Name}: no build output yet");
                return new RunMode.Build();
            }

            var built = File.GetLastWriteTimeUtc(project.OutputFilePath);
            var node = project.FilePath is null ? null : graph.Find(_root.PathOf(project.FilePath));
            if (node is null)
                return new RunMode.Build();
            if (node.EvaluationInputs.Any(i => File.Exists(i.Absolute) && File.GetLastWriteTimeUtc(i.Absolute) > built))
            {
                log($"{project.Name}: project file changed since the last build");
                return new RunMode.Build();
            }

            var (sourcesNewer, otherNewer) = Freshness(node, built);
            if (otherNewer is not null)
            {
                log($"{project.Name}: {_root.PathOf(otherNewer).Relative} changed since the last build");
                return new RunMode.Build();
            }

            // The shadow starts as a copy of the test project's output, which can hold an older build of a project that
            // was built on its own since; that copy is replaced with one emitted from the working tree.
            if (sourcesNewer || (project.Id != testProject.Id && !HoldsBuildOf(testOutputDirectory, project.OutputFilePath)))
                stale.Add(project.Id);
        }

        // A stale assembly's dependents inside the closure are re-emitted too, so none of them binds to a member that moved.
        var emit = new HashSet<ProjectId>(stale);
        bool grew;
        do
        {
            grew = false;
            foreach (var project in closure)
            {
                if (!emit.Contains(project.Id) && project.ProjectReferences.Any(r => emit.Contains(r.ProjectId)))
                    grew = emit.Add(project.Id);
            }
        }
        while (grew);

        var shadow = Path.Combine(_root.StateDirectory, "shadow", $"{testProject.Name.Replace('(', '-').Replace(")", "")}");
        var targets = emit.Select(id => Path.Combine(shadow, Path.GetFileName(solution.GetProject(id)!.OutputFilePath!))).ToList();
        // The files about to be emitted are not copied first: the copy would only be overwritten, and when the emitted
        // image is already in place, it would make the image be written again.
        Mirror(testOutputDirectory, shadow, [.. targets, .. targets.Select(t => Path.ChangeExtension(t, ".pdb"))]);
        foreach (var id in emit)
        {
            var project = solution.GetProject(id)!;
            emitting.Start();
            try
            {
                var image = await _cache.GetAsync(project, cancellationToken).ConfigureAwait(false);
                if (image is null)
                {
                    log($"{project.Name}: does not compile; building with MSBuild to report the errors");
                    return new RunMode.Build();
                }

                var target = Path.Combine(shadow, Path.GetFileName(project.OutputFilePath!));
                await WriteAsync(target, image.Pe, cancellationToken).ConfigureAwait(false);
                await WriteAsync(Path.ChangeExtension(target, ".pdb"), image.Pdb, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                emitting.Stop();
            }
        }

        return new RunMode.Shadow(Path.Combine(shadow, Path.GetFileName(testOutput)));
    }

    /// <summary>
    ///     Writes <paramref name="bytes"/> to a shadow file, unless this emitter already wrote those same bytes there and the
    ///     file still has the length and time it had after.
    /// </summary>
    private async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (_written.TryGetValue(path, out var written) && ReferenceEquals(written.Bytes, bytes)
            && file.Exists && file.Length == written.Length && file.LastWriteTimeUtc == written.WrittenUtc)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        file.Refresh();
        _written[path] = new Written(bytes, file.Length, file.LastWriteTimeUtc);
    }

    private static IEnumerable<Project> Closure(Project project)
    {
        var seen = new HashSet<ProjectId>();
        var stack = new Stack<Project>([project]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current.Id))
                continue;
            yield return current;
            foreach (var reference in current.ProjectReferences)
            {
                if (current.Solution.GetProject(reference.ProjectId) is { } referenced)
                    stack.Push(referenced);
            }
        }
    }

    /// <summary>
    ///     Whether the project's C# sources are newer than its build output, and the first non-source file
    ///     in the project directory that did (a resource or content file the emitted assembly would not include). A
    ///     directory newer than the output means its file set changed, which also makes the sources stale.
    /// </summary>
    /// <remarks>
    ///     The entries stay the strings the directory listing returns, because each only goes back to a file system
    ///     call; only the one entry named in the log becomes a <see cref="RepoPath"/>.
    /// </remarks>
    private static (bool SourcesNewer, string? OtherNewer) Freshness(ProjectNode node, DateTime built)
    {
        var sourcesNewer = false;
        foreach (var entry in EnumerateProjectEntries(node.Directory.Absolute))
        {
            if (File.GetLastWriteTimeUtc(entry) <= built && Directory.GetLastWriteTimeUtc(entry) <= built)
                continue;
            if (Directory.Exists(entry) || PathRules.IsSource(entry))
                sourcesNewer = true;
            else if (!PathRules.IsProjectFile(entry))
                return (sourcesNewer, entry);
        }

        return (sourcesNewer, null);
    }

    /// <summary>
    ///     Whether <paramref name="testOutputDirectory"/> holds the same build of the assembly at <paramref name="built"/>:
    ///     a file of the same name, length and last write time. MSBuild keeps the write time when it copies a referenced
    ///     assembly into a test project's output, so a copy that is missing or differs is not that build.
    /// </summary>
    /// <remarks>An assembly can keep its length across a small edit, so the length alone does not tell two builds apart.</remarks>
    private static bool HoldsBuildOf(string testOutputDirectory, string built)
    {
        var copy = new FileInfo(Path.Combine(testOutputDirectory, Path.GetFileName(built)));
        var original = new FileInfo(built);
        return copy.Exists && copy.Length == original.Length && copy.LastWriteTimeUtc == original.LastWriteTimeUtc;
    }

    /// <summary>Every directory and file under the project directory, skipping build output, hidden folders and nested projects.</summary>
    private static IEnumerable<string> EnumerateProjectEntries(string directory)
    {
        var stack = new Stack<string>([directory]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            yield return dir;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir).ToList();
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("obj", StringComparison.OrdinalIgnoreCase) || name.Equals("TestResults", StringComparison.OrdinalIgnoreCase) || name.StartsWith('.')
                        || File.Exists(Path.Combine(sub, name + ".csproj")) || Directory.EnumerateFiles(sub, "*.csproj").Any())
                        continue;
                    stack.Push(sub);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
                yield return file;
        }
    }

    /// <summary>
    ///     Overlays <paramref name="source"/> onto <paramref name="target"/>, copying files whose size or time differ; files
    ///     only in the target stay, and so do the destinations in <paramref name="emitted"/>, which are about to be emitted.
    /// </summary>
    private static void Mirror(string source, string target, IReadOnlyCollection<string> emitted)
    {
        var skipped = new HashSet<string>(emitted, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            if (skipped.Contains(destination))
                continue;
            var from = new FileInfo(file);
            var to = new FileInfo(destination);
            if (to.Exists && to.Length == from.Length && to.LastWriteTimeUtc == from.LastWriteTimeUtc)
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            File.SetLastWriteTimeUtc(destination, from.LastWriteTimeUtc);
        }
    }

    /// <summary>A shadow file this emitter wrote: the bytes, and the length and last write time the file had after.</summary>
    private sealed record Written(byte[] Bytes, long Length, DateTime WrittenUtc);
}
