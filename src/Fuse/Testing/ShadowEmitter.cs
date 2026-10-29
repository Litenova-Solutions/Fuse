using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Fuse.Changes;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Testing.Model;
using Fuse.Workspace;
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
///         from build output never overwrites a file it is about to emit. It also remembers what a kept dependent's
///         referenced projects declared when its image was written, so the image is kept only while they declare the
///         same. It runs after the request lock is released, against the snapshot the plan took under it;
///         <see cref="TestPlanner"/> prepares one plan at a time.
///     </para>
///     <para>
///         A test run holds the copy it runs from until it ends, and a plan never writes a copy a run holds: it prepares a
///         new one instead (<see cref="CopyFolder"/>). So the runs of several clients run at once, each from its own copy,
///         and none of them holds a file in the repository's build output open.
///     </para>
/// </remarks>
internal sealed class ShadowEmitter
{
    private readonly RepoWorkspace _workspace;
    private readonly RepoRoot _root;
    private readonly EmitCache _cache = new();

    /// <summary>Each shadow file this emitter wrote, with the bytes it wrote and the length and time the file had after.</summary>
    private readonly Dictionary<string, Written> _written = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>For each dependent this emitter wrote, the declaration fingerprint of each project it references, as it was when the dependent was written.</summary>
    private readonly Dictionary<ProjectId, Dictionary<ProjectId, string>> _fingerprints = new();

    public ShadowEmitter(RepoWorkspace workspace)
    {
        _workspace = workspace;
        _root = workspace.Root;
    }

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

        // A stale assembly's dependents inside the closure are re-emitted too, so none of them binds to a member that
        // moved, unless the assemblies they depend on declare the same as HEAD: their images would not change, so the
        // ones already in the shadow folder are kept.
        var emit = new HashSet<ProjectId>(stale);
        var declares = new Dictionary<ProjectId, bool>();
        bool grew;
        do
        {
            grew = false;
            foreach (var project in closure)
            {
                if (emit.Contains(project.Id))
                    continue;
                foreach (var reference in project.ProjectReferences.Where(r => emit.Contains(r.ProjectId)))
                {
                    if (!declares.TryGetValue(reference.ProjectId, out var changed))
                    {
                        changed = true;
                        if (solution.GetProject(reference.ProjectId) is { } referenced)
                            changed = await DeclaresChangeAsync(referenced, cancellationToken).ConfigureAwait(false);
                        declares[reference.ProjectId] = changed;
                    }

                    if (changed)
                    {
                        grew = emit.Add(project.Id);
                        break;
                    }
                }
            }
        }
        while (grew);

        var shadow = CopyFolder(Path.Combine(_root.StateDirectory, "shadow", $"{testProject.Name.Replace('(', '-').Replace(")", "")}"), log);
        // A kept dependent is emitted again when the shadow folder does not already hold the image this emitter wrote,
        // as on a fresh copy: the build output it would be copied from can predate HEAD. It is also emitted again when a
        // referenced project declares differently than when the kept image was written, even while it declares the same
        // as HEAD: the kept image was compiled against an intermediate state.
        var current = new Dictionary<ProjectId, string?>();
        var skipped = new List<Project>();
        foreach (var project in closure)
        {
            if (emit.Contains(project.Id))
                continue;
            var dependedOn = project.ProjectReferences.Select(r => r.ProjectId).Where(emit.Contains).ToList();
            if (dependedOn.Count == 0 || dependedOn.Any(id => !declares.TryGetValue(id, out var changed) || changed))
                continue;
            var kept = Path.Combine(shadow, Path.GetFileName(project.OutputFilePath!));
            if (HoldsWritten(kept) && HoldsWritten(Path.ChangeExtension(kept, ".pdb"))
                && await FingerprintsMatchAsync(project, dependedOn, solution, current, cancellationToken).ConfigureAwait(false))
                skipped.Add(project);
            else
                emit.Add(project.Id);
        }

        foreach (var project in skipped)
        {
            var names = project.ProjectReferences
                .Where(r => emit.Contains(r.ProjectId) && declares.TryGetValue(r.ProjectId, out var changed) && !changed)
                .Select(r => solution.GetProject(r.ProjectId)?.Name)
                .OfType<string>()
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            if (names.Count > 0)
                log($"test plan: {project.Name}: not emitting {project.Name} again; {string.Join(", ", names)} {(names.Count == 1 ? "compiles" : "compile")} to the same declarations as HEAD");
        }

        var targets = emit.Select(id => Path.Combine(shadow, Path.GetFileName(solution.GetProject(id)!.OutputFilePath!))).ToList();
        var keptTargets = skipped.Select(p => Path.Combine(shadow, Path.GetFileName(p.OutputFilePath!))).ToList();
        // The files about to be emitted are not copied first: the copy would only be overwritten, and when the emitted
        // image is already in place, it would make the image be written again. The kept images are not copied either.
        Mirror(testOutputDirectory, shadow, [.. targets, .. targets.Select(t => Path.ChangeExtension(t, ".pdb")), .. keptTargets, .. keptTargets.Select(t => Path.ChangeExtension(t, ".pdb"))]);
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
                await RecordFingerprintsAsync(project, solution, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                emitting.Stop();
            }
        }

        return new RunMode.Shadow(Path.Combine(shadow, Path.GetFileName(testOutput)));
    }

    /// <summary>
    ///     True when <paramref name="project"/> declares anything HEAD does not: a declaration added, removed or changed,
    ///     or a parameter list that reads differently, whose names and defaults callers can name or inline although the
    ///     surface leaves them out. Anything undecidable counts as a change, so the project's dependents are emitted again.
    /// </summary>
    /// <param name="project">A project of the snapshot the plan took.</param>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    private async Task<bool> DeclaresChangeAsync(Project project, CancellationToken cancellationToken)
    {
        try
        {
            var baseline = _workspace.Baseline;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var document in project.Documents)
            {
                if (document.FilePath is null)
                    return true;
                seen.Add(document.FilePath);
                if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } current)
                    return true;
                var before = baseline.GetDocument(document.Id)
                    ?? baseline.GetDocumentIdsWithFilePath(document.FilePath).Select(baseline.GetDocument).FirstOrDefault(d => d is not null && d.Project.Id == document.Project.Id);
                if (before is null || await before.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } head)
                    return true;
                var beforeDeclarations = FileDeclarations.Of(head);
                var currentDeclarations = FileDeclarations.Of(current);
                if (SurfaceDiff.Compare(beforeDeclarations, currentDeclarations).Changes.Count > 0)
                    return true;
                if (ParameterListsDiffer(beforeDeclarations, currentDeclarations))
                    return true;
            }

            if (baseline.GetProject(project.Id) is not { } baselineProject)
                return true;
            foreach (var document in baselineProject.Documents)
            {
                if (document.FilePath is not null && !seen.Contains(document.FilePath))
                    return true;
            }

            return false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>True when a method-like declaration both versions declare has a parameter list that reads differently.</summary>
    private static bool ParameterListsDiffer(FileDeclarations before, FileDeclarations after)
    {
        foreach (var declaration in after.All)
        {
            if (before.Find(declaration.Key) is not { } previous)
                continue;
            if (!string.Equals(FileDeclarations.FlatParameters(previous.Node), FileDeclarations.FlatParameters(declaration.Node), StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     True when every project in <paramref name="dependedOn"/> declares the same as when
    ///     <paramref name="dependent"/>'s image was written. A missing record, or a fingerprint that cannot be read,
    ///     means the image is written again.
    /// </summary>
    private async Task<bool> FingerprintsMatchAsync(Project dependent, IReadOnlyList<ProjectId> dependedOn, Solution solution, Dictionary<ProjectId, string?> current, CancellationToken cancellationToken)
    {
        if (!_fingerprints.TryGetValue(dependent.Id, out var recorded))
            return false;
        foreach (var id in dependedOn)
        {
            if (!current.TryGetValue(id, out var fingerprint))
            {
                fingerprint = solution.GetProject(id) is { } referenced ? await FingerprintAsync(referenced, cancellationToken).ConfigureAwait(false) : null;
                current[id] = fingerprint;
            }

            if (fingerprint is null || !recorded.TryGetValue(id, out var written) || written != fingerprint)
                return false;
        }

        return true;
    }

    /// <summary>Records the declaration fingerprint of each project <paramref name="project"/> references, as it is now.</summary>
    private async Task RecordFingerprintsAsync(Project project, Solution solution, CancellationToken cancellationToken)
    {
        var recorded = new Dictionary<ProjectId, string>();
        foreach (var reference in project.ProjectReferences)
        {
            if (solution.GetProject(reference.ProjectId) is { } referenced
                && await FingerprintAsync(referenced, cancellationToken).ConfigureAwait(false) is { } fingerprint)
                recorded[reference.ProjectId] = fingerprint;
        }

        _fingerprints[project.Id] = recorded;
    }

    /// <summary>
    ///     The declarations <paramref name="project"/> holds now: every declaration's identity, surface and parameter
    ///     lists, over every document in path order, hashed. A default value or a constant is part of it, because callers
    ///     inline them. Null when the declarations cannot be read, which the caller treats as changed.
    /// </summary>
    private static async Task<string?> FingerprintAsync(Project project, CancellationToken cancellationToken)
    {
        try
        {
            var builder = new StringBuilder();
            foreach (var document in project.Documents.OrderBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                if (document.FilePath is null)
                    return null;
                if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root)
                    return null;
                foreach (var declaration in FileDeclarations.Of(root).All)
                {
                    builder.Append(declaration.Key).Append('\n');
                    builder.Append(declaration.Surface ?? "<none>").Append('\n');
                    foreach (var part in declaration.Parts)
                        builder.Append(FileDeclarations.FlatParameters(part)).Append('\n');
                }

                builder.Append('\0');
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Whether this emitter wrote <paramref name="path"/> and the file still holds those bytes.</summary>
    private bool HoldsWritten(string path)
    {
        if (!_written.TryGetValue(path, out var written))
            return false;
        var file = new FileInfo(path);
        return file.Exists && file.Length == written.Length && file.LastWriteTimeUtc == written.WrittenUtc;
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

    /// <summary>
    ///     The folder a plan prepares a test project's shadow run in, under <paramref name="project"/>: the newest copy when
    ///     no test run holds it, so the files already in place are not written again, or a new copy when a run still runs
    ///     from it (<see cref="LocalState.HoldTestCopy"/>), so the run keeps the files it started with. Every older copy no
    ///     run holds is deleted.
    /// </summary>
    /// <remarks>
    ///     Copies are numbered folders, <c>g1</c>, <c>g2</c> and so on, the newest with the highest number. Anything else in
    ///     <paramref name="project"/> is left from a version of Fuse that kept one copy per test project, and is deleted.
    ///     The client holds <c>build.lock</c> from before it asks for a plan until it holds the copies the plan names, so
    ///     no run takes a copy between this choice and the plan's answer.
    /// </remarks>
    private static string CopyFolder(string project, Action<string> log)
    {
        Directory.CreateDirectory(project);
        var copies = new List<(int Number, string Path)>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(project))
        {
            var name = Path.GetFileName(entry);
            if (name.Length > 1 && name[0] == 'g' && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && Directory.Exists(entry))
                copies.Add((number, entry));
            else
                TryDelete(entry);
        }

        var newest = copies.Count == 0 ? (Number: 0, Path: null) : copies.MaxBy(c => c.Number);
        var chosen = newest.Path;
        if (chosen is null || LocalState.IsInUse(chosen))
        {
            chosen = Path.Combine(project, $"g{newest.Number + 1}");
            Directory.CreateDirectory(chosen);
            if (newest.Path is not null)
                log($"{Path.GetFileName(project)}: a test run still runs from {Path.GetFileName(newest.Path)}, so this plan copies into {Path.GetFileName(chosen)}");
        }

        foreach (var (_, path) in copies)
        {
            if (path != chosen && !LocalState.IsInUse(path))
                TryDelete(path);
        }

        return chosen;
    }

    /// <summary>Deletes a file or folder left in a shadow folder; one a process still holds stays until a later plan.</summary>
    private static void TryDelete(string entry)
    {
        try
        {
            if (Directory.Exists(entry))
                Directory.Delete(entry, recursive: true);
            else
                File.Delete(entry);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
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
