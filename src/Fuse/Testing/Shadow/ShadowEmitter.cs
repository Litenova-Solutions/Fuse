using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Fuse.Changes;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Testing.Model;
using Fuse.Workspace.Generations;
using Microsoft.CodeAnalysis;
namespace Fuse.Testing.Shadow;

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
///         One emitter serves one plan, bound to the generation that plan pinned. Emitters share one state: the
///         emitted images in an <see cref="EmitCache"/>, and each shadow file an emitter wrote, up to a limit for
///         each map the state keeps, so an image that is already in place is not written again and the copy
///         from build output never overwrites a file it is about to emit. The state also remembers what a kept
///         dependent's referenced projects declared when its image was written, so the image is kept only while
///         they declare the same. Each plan holds the copy it writes until its preparation ends, so plans that
///         prepare at the same time write copies of their own. A copy a plan answered with is kept after its
///         preparation ends, while a plan that was already under way could still take it for its own answer and
///         no run has held it since; a later plan that starts after it may use it again, and an older copy goes
///         only at such a plan, once no run holds it and a run has held it since it was answered with.
///     </para>
///     <para>
///         A test run holds the copy it runs from until it ends, and a plan never writes a copy a run holds: it prepares a
///         new one instead (<see cref="CopyFolder"/>). So the runs of several clients run at once, each from its own copy,
///         and none of them holds a file in the repository's build output open.
///     </para>
/// </remarks>
internal sealed class ShadowEmitter
{
    /// <summary>The most shadow files whose written bytes are remembered; past it the file written longest ago goes.</summary>
    internal const int MaxWrittenEntries = 512;

    /// <summary>The most dependents whose declaration fingerprints are remembered; past it the dependent written longest ago goes.</summary>
    internal const int MaxFingerprintEntries = 128;

    private readonly RepoWorkspace _workspace;
    private readonly RepoRoot _root;
    private readonly SharedState _shared;
    private readonly Solution? _baseline;

    /// <summary>The copies this plan holds while it prepares them, released when its preparation ends.</summary>
    private readonly List<IDisposable> _holds = [];

    /// <summary>The copy this plan claimed for each shadow folder, so every target framework of one test project prepares into the same copy.</summary>
    private readonly Dictionary<string, string> _copies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where this plan stands in the shared order of plans, taken when its emitter was made; a copy answered after it is not this plan's to use or remove.</summary>
    private readonly long _sequence;

    /// <summary>What the emitters of one planner share, so an image one plan emits serves the next and concurrent plans agree on what each shadow file holds.</summary>
    internal sealed class SharedState
    {
        /// <summary>The emitted images kept between plans.</summary>
        internal EmitCache Cache { get; } = new();

        /// <summary>Each shadow file an emitter wrote, with its 32-byte SHA-256 hash and the length and time the file had after.</summary>
        internal ConcurrentDictionary<string, Written> Written { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The written files in the order they were last written in, for <see cref="WrittenGate"/>.</summary>
        internal List<string> WrittenOrder { get; } = [];

        /// <summary>Guards <see cref="WrittenOrder"/>, the order the written files were last written in.</summary>
        internal Lock WrittenGate { get; } = new();

        /// <summary>For each dependent an emitter wrote, the declaration fingerprint of each project it references, as it was when the dependent was written.</summary>
        internal ConcurrentDictionary<ProjectId, Dictionary<ProjectId, string>> Fingerprints { get; } = new();
        /// <summary>The dependents in the order they were last written in, for <see cref="FingerprintGate"/>.</summary>
        internal List<ProjectId> FingerprintOrder { get; } = [];

        /// <summary>Guards <see cref="FingerprintOrder"/>, the order the dependents were last written in.</summary>
        internal Lock FingerprintGate { get; } = new();

        /// <summary>Serializes the choice of a shadow copy, so two plans never choose the copy the other is writing.</summary>
        internal Lock Allocation { get; } = new();

        /// <summary>The places in the order of plans and of the answers they gave taken so far, for <see cref="Answered"/>.</summary>
        private long _sequence;

        /// <summary>The next place in the order of plans and of the answers they gave.</summary>
        internal long NextSequence() => Interlocked.Increment(ref _sequence);

        /// <summary>
        ///     Each copy a plan answered with, with the place in the order its preparation ended. A copy stays
        ///     here until a plan that is not preparing it finds a run holding it, which says its answer was taken;
        ///     from then on a later plan may remove it once no run holds it.
        /// </summary>
        internal ConcurrentDictionary<string, long> Answered { get; } = new(StringComparer.OrdinalIgnoreCase);
    }






    public ShadowEmitter(RepoWorkspace workspace)
        : this(workspace, (Solution?)null, new SharedState())
    {
    }

    /// <summary>An emitter over <paramref name="shared"/> state whose declarations compare against the workspace's baseline.</summary>
    internal ShadowEmitter(RepoWorkspace workspace, SharedState shared)
        : this(workspace, (Solution?)null, shared)
    {
    }

    /// <summary>An emitter for one plan: it compares declarations against <paramref name="generation"/>'s baseline and shares <paramref name="shared"/> with the planner's other plans.</summary>
    internal ShadowEmitter(RepoWorkspace workspace, Generation generation, SharedState shared)
        : this(workspace, generation.Baseline, shared)
    {
    }

    private ShadowEmitter(RepoWorkspace workspace, Solution? baseline, SharedState shared)
    {
        _workspace = workspace;
        _root = workspace.Root;
        _baseline = baseline;
        _shared = shared;
        _sequence = shared.NextSequence();
    }

    /// <summary>
    ///     Releases the copies this plan held while it prepared them, so a later plan may write them again. Each
    ///     copy is first recorded as one this plan answered with, under the same lock that chooses copies, so a
    ///     plan that is already under way keeps it for this plan's answer instead of using or removing it.
    /// </summary>
    internal void ReleaseCopies()
    {
        lock (_shared.Allocation)
        {
            var answered = _shared.NextSequence();
            foreach (var copy in _copies.Values)
                _shared.Answered[copy] = answered;
            foreach (var hold in _holds)
                hold.Dispose();
            _holds.Clear();
            _copies.Clear();
        }
    }

    /// <summary>How many assemblies were emitted since the engine started, as opposed to taken from the cache.</summary>
    public int Emits => _shared.Cache.Emits;

    /// <summary>How many shadow files' written bytes are remembered.</summary>
    public int KeptWritten => _shared.Written.Count;

    /// <summary>How many dependents' declaration fingerprints are remembered.</summary>
    public int KeptFingerprints => _shared.Fingerprints.Count;

    /// <summary>Outcome of preparing a shadow run, including the closure projects and any newly or cached emitted images.</summary>
    internal sealed record PreparationResult(
        RunMode Mode,
        IReadOnlyList<Project> Closure,
        IReadOnlyDictionary<ProjectId, (EmittedImage Image, string PeHash, string PdbHash)> Emitted);

    /// <summary>Returns a shadow run of the prepared test assembly, or a build with MSBuild when a shadow run is not safe.</summary>
    /// <param name="testProject">The Roslyn project for one target framework of the test project, from the plan's generation.</param>
    /// <param name="graph">The project graph of the plan's generation.</param>
    /// <param name="log">Receives the reason when a shadow run is refused.</param>
    /// <param name="emitting">Runs while assemblies are emitted and written, so the caller can tell the emit from the rest.</param>
    /// <param name="cancellationToken">Cancels emitting.</param>
    public async Task<RunMode> PrepareAsync(Project testProject, RepoGraph graph, Action<string> log, Stopwatch emitting, CancellationToken cancellationToken)
    {
        var result = await PrepareDetailedAsync(testProject, graph, log, emitting, cancellationToken).ConfigureAwait(false);
        return result.Mode;
    }

    /// <summary>Prepares a shadow run and returns its mode alongside the closure and emitted images with SHA-256 hashes.</summary>
    internal async Task<PreparationResult> PrepareDetailedAsync(Project testProject, RepoGraph graph, Action<string> log, Stopwatch emitting, CancellationToken cancellationToken)
    {
        var solution = testProject.Solution;
        var testOutput = testProject.OutputFilePath;
        if (testOutput is null || !File.Exists(testOutput))
        {
            log($"{testProject.Name}: no build output yet");
            return new PreparationResult(new RunMode.Build(), [], new Dictionary<ProjectId, (EmittedImage, string, string)>());
        }

        // Every project the test assembly loads, with the build output it would be copied from.
        var (closure, refusal) = TryClosure(testProject, graph);
        if (refusal is not null)
        {
            log(refusal);
            return new PreparationResult(new RunMode.Build(), closure, new Dictionary<ProjectId, (EmittedImage, string, string)>());
        }

        // Two variants of one project in the closure mean two builds stand where the test project loads one;
        // the choice of which one emits is logged, so a wrong one can be told from the engine log.
        foreach (var group in closure.GroupBy(p => p.FilePath ?? p.Id.ToString(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            log($"test plan: {testProject.Name} closure holds {group.Count()} variants of {Path.GetFileName(group.Key)}: {string.Join(", ", group.Select(p => p.Name).Order(StringComparer.Ordinal))}");
        var testOutputDirectory = Path.GetDirectoryName(testOutput)!;
        var stale = new HashSet<ProjectId>();
        foreach (var project in closure)
        {
            if (project.OutputFilePath is null || !File.Exists(project.OutputFilePath))
            {
                log($"{project.Name}: no build output yet");
                return new PreparationResult(new RunMode.Build(), closure, new Dictionary<ProjectId, (EmittedImage, string, string)>());
            }

            var built = File.GetLastWriteTimeUtc(project.OutputFilePath);
            var node = project.FilePath is null ? null : graph.Find(_root.PathOf(project.FilePath));
            if (node is null)
                return new PreparationResult(new RunMode.Build(), closure, new Dictionary<ProjectId, (EmittedImage, string, string)>());
            if (node.EvaluationInputs.Any(i => File.Exists(i.Absolute) && File.GetLastWriteTimeUtc(i.Absolute) > built))
            {
                log($"{project.Name}: project file changed since the last build");
                return new PreparationResult(new RunMode.Build(), closure, new Dictionary<ProjectId, (EmittedImage, string, string)>());
            }

            var (sourcesNewer, otherNewer) = Freshness(node, built);
            if (otherNewer is not null)
            {
                log($"{project.Name}: {_root.PathOf(otherNewer).Relative} changed since the last build");
                return new PreparationResult(new RunMode.Build(), closure, new Dictionary<ProjectId, (EmittedImage, string, string)>());
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

        var shadow = HoldCopy(Path.Combine(_root.StateDirectory, "shadow", $"{testProject.Name.Replace('(', '-').Replace(")", "")}"), log);
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

        // Two variants writing the same shadow file overwrite each other, and the loser can be a build for
        // another framework, which then crashes at runtime. When the closure holds two, no shadow run matches the
        // build, and the project builds with MSBuild instead.
        var outputs = emit.Select(id => solution.GetProject(id)!).Concat(skipped).ToList();
        if (outputs.GroupBy(p => Path.GetFileName(p.OutputFilePath!), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } collision)
        {
            log($"{testProject.Name}: two variants share the shadow file {collision.Key}; building with MSBuild");
            return new PreparationResult(new RunMode.Build(), closure, new Dictionary<ProjectId, (EmittedImage, string, string)>());
        }

        var targets = emit.Select(id => Path.Combine(shadow, Path.GetFileName(solution.GetProject(id)!.OutputFilePath!))).ToList();
        var keptTargets = skipped.Select(p => Path.Combine(shadow, Path.GetFileName(p.OutputFilePath!))).ToList();
        // The files about to be emitted are not copied first: the copy would only be overwritten, and when the emitted
        // image is already in place, it would make the image be written again. The kept images are not copied either.
        Mirror(testOutputDirectory, shadow, [.. targets, .. targets.Select(t => Path.ChangeExtension(t, ".pdb")), .. keptTargets, .. keptTargets.Select(t => Path.ChangeExtension(t, ".pdb"))]);
        var emittedImages = new Dictionary<ProjectId, (EmittedImage Image, string PeHash, string PdbHash)>();
        foreach (var id in emit)
        {
            var project = solution.GetProject(id)!;
            emitting.Start();
            try
            {
                var (image, peHash, pdbHash) = await _shared.Cache.GetWithHashesAsync(project, cancellationToken).ConfigureAwait(false);
                if (image is null)
                {
                    log($"{project.Name}: does not compile; building with MSBuild to report the errors");
                    return new PreparationResult(new RunMode.Build(), closure, emittedImages);
                }

                emittedImages[id] = (image, peHash, pdbHash);
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

        return new PreparationResult(new RunMode.Shadow(Path.Combine(shadow, Path.GetFileName(testOutput))), closure, emittedImages);
    }

    /// <summary>
    ///     True when <paramref name="project"/> declares anything HEAD does not: a declaration added, removed or changed,
    ///     or a parameter list that reads differently, whose names and defaults callers can name or inline although the
    ///     surface leaves them out. Anything undecidable counts as a change, so the project's dependents are emitted again.
    /// </summary>
    /// <param name="project">A project of the generation the plan pinned.</param>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    private async Task<bool> DeclaresChangeAsync(Project project, CancellationToken cancellationToken)
    {
        try
        {
            var baseline = _baseline ?? _workspace.Baseline;
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
        if (!_shared.Fingerprints.TryGetValue(dependent.Id, out var recorded))
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

        lock (_shared.FingerprintGate)
        {
            _shared.Fingerprints[project.Id] = recorded;
            _shared.FingerprintOrder.Remove(project.Id);
            _shared.FingerprintOrder.Add(project.Id);
            while (_shared.Fingerprints.Count > MaxFingerprintEntries && _shared.FingerprintOrder.Count > 0)
            {
                var oldest = _shared.FingerprintOrder[0];
                _shared.FingerprintOrder.RemoveAt(0);
                _shared.Fingerprints.TryRemove(oldest, out _);
            }
        }
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

    /// <summary>Whether an emitter wrote <paramref name="path"/> and the file still holds those bytes.</summary>
    private bool HoldsWritten(string path)
    {
        if (!_shared.Written.TryGetValue(path, out var written))
            return false;
        var file = new FileInfo(path);
        return file.Exists && file.Length == written.Length && file.LastWriteTimeUtc == written.WrittenUtc;
    }

    /// <summary>
    ///     Writes <paramref name="bytes"/> to a shadow file, unless an emitter already wrote the same content hash there
    ///     and the file still has the length and time it had after. The bytes go to a temporary file first, which is
    ///     moved over the target, so a hardlink there is replaced instead of written through into build output.
    /// </summary>
    private async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var contentHash = SHA256.HashData(bytes);
        var file = new FileInfo(path);
        if (_shared.Written.TryGetValue(path, out var written)
            && written.ContentHash.AsSpan().SequenceEqual(contentHash)
            && file.Exists && file.Length == written.Length && file.LastWriteTimeUtc == written.WrittenUtc)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            MoveOver(temp, path);
        }
        finally
        {
            TryDelete(temp);
        }

        file.Refresh();
        lock (_shared.WrittenGate)
        {
            _shared.Written[path] = new Written(contentHash, file.Length, file.LastWriteTimeUtc);
            _shared.WrittenOrder.Remove(path);
            _shared.WrittenOrder.Add(path);
            while (_shared.Written.Count > MaxWrittenEntries && _shared.WrittenOrder.Count > 0)
            {
                var oldest = _shared.WrittenOrder[0];
                _shared.WrittenOrder.RemoveAt(0);
                _shared.Written.TryRemove(oldest, out _);
            }
        }
    }

    /// <summary>
    ///     Chooses the copy of <paramref name="project"/> this plan prepares and holds it until the plan's
    ///     preparation ends, so a plan preparing at the same time chooses another copy and neither writes the
    ///     copy the other answers with. The choice itself is serialized in the shared state, briefly.
    /// </summary>
    private string HoldCopy(string project, Action<string> log)
    {
        lock (_shared.Allocation)
        {
            if (_copies.TryGetValue(project, out var claimed))
                return claimed;
            var chosen = CopyFolder(project, log);
            Directory.CreateDirectory(chosen);
            _holds.Add(LocalState.HoldTestCopy(chosen));
            _copies[project] = chosen;
            return chosen;
        }
    }

    /// <summary>
    ///     The folder a plan prepares a test project's shadow run in, under <paramref name="project"/>: the newest copy when
    ///     no test run holds it and no plan answered with it while this plan was under way, so the files already in
    ///     place are not written again, or a new copy when a run still runs from it
    ///     (<see cref="LocalState.HoldTestCopy"/>) or a plan under way at the same time answered with it, so each
    ///     answer keeps the files it names. Every older copy is deleted once no run holds it, a run has held it
    ///     since a plan answered with it, and it is not the copy chosen here.
    /// </summary>
    /// <remarks>
    ///     Copies are numbered folders, <c>g1</c>, <c>g2</c> and so on, the newest with the highest number. Anything else in
    ///     <paramref name="project"/> is left from a version of Fuse that kept one copy per test project, and is deleted.
    ///     The client holds <c>build.lock</c> from before it asks for a plan until it holds the copies the plan names, so
    ///     no run takes a copy between this choice and the plan's answer.
    /// </remarks>
    private string CopyFolder(string project, Action<string> log)
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
        var held = chosen is not null && LocalState.IsInUse(chosen);
        var answered = chosen is not null && _shared.Answered.TryGetValue(chosen, out var answeredAt) && answeredAt > _sequence;
        if (chosen is null || held || answered)
        {
            chosen = Path.Combine(project, $"g{newest.Number + 1}");
            Directory.CreateDirectory(chosen);
            if (held)
                log($"{Path.GetFileName(project)}: a test run still runs from {Path.GetFileName(newest.Path)}, so this plan copies into {Path.GetFileName(chosen)}");
        }

        foreach (var (_, path) in copies)
        {
            if (path == chosen)
                continue;
            if (LocalState.IsInUse(path))
            {
                // A run holding a copy a plan answered with has taken that answer, so a later choice may
                // remove the copy once nothing holds it; a plan preparing it answers with it again at the
                // end of its preparation, which records it anew.
                _shared.Answered.TryRemove(path, out _);
                continue;
            }

            if (_shared.Answered.ContainsKey(path))
                continue;
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

    /// <summary>
    ///     Every project <paramref name="project"/>'s assembly loads, through its project references and through
    ///     the references the graph names that this view supplies by a summary instead: a summarized reference is
    ///     no project reference in the view, but the test assembly still loads that project's build output, so
    ///     its freshness and its emitted image are the shadow's business as much as a live reference's are.
    ///     Without this, a non-C# file changed in a library the view supplies by summary was never judged
    ///     stale, and the plan ran from the build output the change had already left behind.
    /// </summary>
    /// <returns>The closure, or the reason a shadow run is refused when no single variant of a referenced project can be named.</returns>
    private (List<Project> Projects, string? Refusal) TryClosure(Project project, RepoGraph graph)
    {
        var seen = new HashSet<ProjectId>();
        var projects = new List<Project>();
        var stack = new Stack<Project>([project]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current.Id))
                continue;
            projects.Add(current);
            foreach (var reference in current.ProjectReferences)
            {
                if (current.Solution.GetProject(reference.ProjectId) is { } referenced)
                    stack.Push(referenced);
            }

            if (current.FilePath is null || graph.Find(_root.PathOf(current.FilePath)) is not { } node)
                continue;
            foreach (var referencePath in node.References)
            {
                if (graph.Find(referencePath) is not { } referencedNode)
                    continue;
                var candidates = RepoWorkspace.ProjectsFor(current.Solution, referencedNode).ToList();
                if (candidates.Count == 0)
                    continue;
                // One project per target framework of the referenced project stands in the solution; the one
                // this project loads is the one built for the same framework folder of build output.
                var folder = FrameworkFolder(current);
                var loaded = folder is null
                    ? candidates
                    : candidates.Where(c => string.Equals(FrameworkFolder(c), folder, StringComparison.OrdinalIgnoreCase)).ToList();
                if (loaded.Count == 0)
                {
                    // The referenced project builds for no such folder: MSBuild loads its nearest compatible
                    // build, so the shadow emits that variant. When no single variant can be named, no shadow
                    // run matches the build, and the project builds with MSBuild instead.
                    if (NearestVariant(folder, candidates) is not { } nearest)
                        return ([], $"{referencedNode.Name}: none of its builds loads in the {folder} output; building with MSBuild");
                    loaded = [nearest];
                }

                foreach (var referenced in loaded)
                    stack.Push(referenced);
            }
        }

        return (projects, null);
    }

    /// <summary>
    ///     The variant of a referenced project a build for <paramref name="folder"/> loads: the one built for the
    ///     nearest framework it accepts, preferring its own family, or null when no single variant can be named. A
    ///     lone candidate stands whatever the folders say: it is the only build the test project can load.
    /// </summary>
    internal static Project? NearestVariant(string? folder, IReadOnlyList<Project> candidates)
    {
        if (candidates.Count == 1)
            return candidates[0];
        if (folder is null || TargetFramework.ParseFolder(folder) is not { } requester)
            return null;
        var accepted = new List<(Project Candidate, TargetFramework Framework)>();
        foreach (var candidate in candidates)
        {
            if (FrameworkFolder(candidate) is { } candidateFolder
                && TargetFramework.ParseFolder(candidateFolder) is { } candidateFramework
                && requester.Accepts(candidateFramework))
                accepted.Add((candidate, candidateFramework));
        }

        return accepted
            .OrderByDescending(x => x.Framework.Family == requester.Family)
            .ThenByDescending(x => x.Framework.Version)
            .Select(x => x.Candidate)
            .FirstOrDefault();
    }

    /// <summary>The framework folder of <paramref name="project"/>'s build output, which names its target framework, or null when it has no build output.</summary>
    private static string? FrameworkFolder(Project project) =>
        project.OutputFilePath is { } output ? Path.GetFileName(Path.GetDirectoryName(output)) : null;

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

    /// <summary>True when a shadow copy may link <paramref name="path"/> instead of copying it: an assembly or a program database, which no test process writes.</summary>
    private static bool IsLinkable(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Overlays <paramref name="source"/> onto <paramref name="target"/> for files whose size or time differ; files
    ///     only in the target stay, and so do the destinations in <paramref name="emitted"/>, which are about to be
    ///     emitted. Assemblies and program databases are linked where the operating system supports it, falling back to
    ///     <see cref="File.Copy(string, string)"/> when cross-volume or unsupported; every other file is copied, because
    ///     a test process writes data, configuration and log files next to its assembly, and through a link such a write
    ///     would land in build output. Each new entry is prepared under a temporary name and moved over its destination,
    ///     so a hardlink there is replaced instead of written through into build output.
    /// </summary>
    internal static void Mirror(string source, string target, IReadOnlyCollection<string> emitted)
    {
        var skipped = new HashSet<string>(emitted, StringComparer.OrdinalIgnoreCase);
        var createdDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hardlinksUnsupported = false;
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
            var directory = Path.GetDirectoryName(destination)!;
            if (createdDirectories.Add(directory))
                Directory.CreateDirectory(directory);
            var temp = destination + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                if (!hardlinksUnsupported && IsLinkable(file))
                {
                    var linkResult = TryCreateHardLink(file, temp);
                    if (linkResult == HardLinkResult.Created)
                    {
                        MoveOver(temp, destination);
                        continue;
                    }

                    if (linkResult == HardLinkResult.Unsupported)
                        hardlinksUnsupported = true;
                }

                // The link path moved its temporary into place and continued above, so this temporary holds the
                // fresh copy, never a link: setting the source write time on it cannot reach build output.
                File.Copy(file, temp);
                File.SetLastWriteTimeUtc(temp, from.LastWriteTimeUtc);
                MoveOver(temp, destination);
            }
            finally
            {
                TryDelete(temp);
            }
        }
    }

    /// <summary>
    ///     Replaces <paramref name="destination"/> with <paramref name="temp"/>, a file in the same folder. The move
    ///     replaces the directory entry, so a hardlink into build output that is still there keeps the file it shares
    ///     untouched, and a reader never sees the destination missing. A read-only entry is cleared first, because
    ///     Windows cannot replace one otherwise.
    /// </summary>
    private static void MoveOver(string temp, string destination)
    {
        try
        {
            File.Move(temp, destination, overwrite: true);
        }
        catch (UnauthorizedAccessException)
        {
            File.SetAttributes(destination, FileAttributes.Normal);
            File.Delete(destination);
            File.Move(temp, destination);
        }
    }

    private static HardLinkResult TryCreateHardLink(string existingSourcePath, string newLinkPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (Native.CreateHardLinkW(newLinkPath, existingSourcePath, 0))
                    return HardLinkResult.Created;
                var error = Marshal.GetLastPInvokeError();
                // 1: ERROR_INVALID_FUNCTION, 5: ERROR_ACCESS_DENIED, 17: ERROR_NOT_SAME_DEVICE, 50: ERROR_NOT_SUPPORTED.
                return error is 1 or 5 or 17 or 50 ? HardLinkResult.Unsupported : HardLinkResult.PerFileFallback;
            }

            if (Native.link(existingSourcePath, newLinkPath) == 0)
                return HardLinkResult.Created;
            var errno = Marshal.GetLastPInvokeError();
            // 1: EPERM, 13: EACCES, 18: EXDEV, 45/95/102: ENOTSUP/EOPNOTSUPP across Linux and macOS.
            return errno is 1 or 13 or 18 or 45 or 95 or 102 ? HardLinkResult.Unsupported : HardLinkResult.PerFileFallback;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return HardLinkResult.Unsupported;
        }
    }

    /// <summary>A shadow file this emitter wrote: the 32-byte SHA-256 content hash, and the length and last write time the file had after.</summary>
    internal sealed record Written(byte[] ContentHash, long Length, DateTime WrittenUtc);

    private enum HardLinkResult
    {
        Created,
        PerFileFallback,
        Unsupported,
    }

    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, nint lpSecurityAttributes);

        [DllImport("libc", EntryPoint = "link", SetLastError = true)]
#pragma warning disable CA2101, SYSLIB1054 // UTF-8 marshalling is what libc expects.
        public static extern int link([MarshalAs(UnmanagedType.LPUTF8Str)] string oldpath, [MarshalAs(UnmanagedType.LPUTF8Str)] string newpath);
#pragma warning restore CA2101, SYSLIB1054
    }
}
