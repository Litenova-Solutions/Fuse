using System.Collections.Concurrent;
using System.Text;
using Fuse.Graph;
using Fuse.Protocol;
using Fuse.Repo;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace Fuse.Workspace;

/// <summary>
///     Holds two views of the loaded projects: <see cref="Current"/> (the working tree) and <see cref="Baseline"/>
///     (the same projects with every changed source restored to its HEAD content; project files are evaluated as they
///     are on disk). Projects load when a check or test plan needs them, or in the background ahead of time.
/// </summary>
/// <remarks>
///     MSBuildWorkspace is used only as a loader. Its <c>TryApplyChanges</c> writes to disk, so it is never called;
///     both views are immutable <see cref="Solution"/> snapshots derived from the loader's solution and replaced
///     atomically. The baseline's content only changes when HEAD moves or projects reload, so diagnostics computed
///     from it stay cached across checks.
/// </remarks>
internal sealed class RepoWorkspace : IDisposable
{
    private readonly RepoRoot _root;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, byte> _loaded = new(ChangeTracker.PathComparer);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile bool _rebuildPending;
    private readonly HashSet<string> _touched = new(ChangeTracker.PathComparer);
    private readonly Dictionary<string, string> _loadFailures = new(ChangeTracker.PathComparer);
    private MSBuildWorkspace? _loader;

    public RepoWorkspace(RepoRoot root, Action<string> log)
    {
        _root = root;
        _log = log;
        Tracker = new ChangeTracker(root);
    }

    public ChangeTracker Tracker { get; }

    public RepoGraph Graph { get; private set; } = null!;

    /// <summary>The loaded projects as they are on disk.</summary>
    public Solution Current { get; private set; } = null!;

    /// <summary>The loaded projects with every changed file at its HEAD content.</summary>
    public Solution Baseline { get; private set; } = null!;

    /// <summary>Increments whenever the HEAD view's content changes (HEAD moved, projects reloaded), which invalidates everything cached against it.</summary>
    public int BaselineGeneration { get; private set; }

    /// <summary>Increments whenever projects are re-evaluated and reloaded, which invalidates anything derived from project configuration.</summary>
    public int LoaderGeneration { get; private set; }

    public RepoRoot Root => _root;

    /// <summary>Writes a line to the engine log.</summary>
    public void Log(string message) => _log(message);

    /// <summary>Evaluates the project graph and starts tracking changes. Compiles nothing.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await Tracker.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EvaluateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Folds disk changes into both views.</summary>
    /// <param name="knownPaths">Paths just written by the agent, checked even before their watcher event arrives.</param>
    /// <param name="cancellationToken">Cancels re-evaluation.</param>
    public async Task SyncAsync(IEnumerable<string> knownPaths, CancellationToken cancellationToken)
    {
        var batch = await Tracker.SyncAsync(knownPaths, cancellationToken).ConfigureAwait(false);
        if (batch.ProjectFilesChanged || batch.Storm)
        {
            _log($"reloading: project files changed={batch.ProjectFilesChanged} storm={batch.Storm} headMoved={batch.HeadMoved} trigger={batch.Trigger}");
            var reopen = _loaded.Keys.ToList();
            _touched.UnionWith(batch.SourcePaths);
            if (batch.ProjectFilesChanged)
                await EvaluateAsync(cancellationToken).ConfigureAwait(false);
            else
                await ResetLoaderAsync(cancellationToken).ConfigureAwait(false);
            // Reopen the projects that were loaded, so the next check does not pay for a cold load it did not ask for.
            var nodes = reopen.Select(Graph.Find).OfType<ProjectNode>().ToList();
            if (nodes.Count > 0)
                await EnsureLoadedAsync(nodes, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (batch.HeadMoved || _rebuildPending)
        {
            // HEAD moved, or a background load added projects: derive both views from the loader again.
            _touched.UnionWith(batch.SourcePaths);
            _rebuildPending = false;
            await RebuildAsync(batch.HeadMoved, cancellationToken).ConfigureAwait(false);
            if (batch.HeadMoved)
                return;
        }

        var sourcePaths = new HashSet<string>(batch.SourcePaths, ChangeTracker.PathComparer);
        foreach (var directory in batch.VanishedDirectories)
        {
            var prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            sourcePaths.UnionWith(Current.Projects.SelectMany(p => p.Documents.Concat<TextDocument>(p.AdditionalDocuments))
                .Select(d => d.FilePath)
                .OfType<string>()
                .Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        }

        if (sourcePaths.Count == 0 || _loader is null)
            return;
        var current = Current;
        var baseline = Baseline;
        foreach (var path in sourcePaths)
        {
            _touched.Add(path);
            current = await WithDiskContentAsync(current, path, cancellationToken).ConfigureAwait(false);
            baseline = await WithHeadContentAsync(baseline, path, cancellationToken).ConfigureAwait(false);
        }

        Current = current;
        if (!ReferenceEquals(baseline, Baseline))
        {
            Baseline = baseline;
            BaselineGeneration++;
        }
    }

    /// <summary>Loads <paramref name="projects"/> and everything they reference, and makes them part of both views.</summary>
    /// <exception cref="FuseException">A project has not been restored or failed to load.</exception>
    public async Task EnsureLoadedAsync(IEnumerable<ProjectNode> projects, CancellationToken cancellationToken)
    {
        var requested = projects.DistinctBy(p => p.Path).ToList();
        foreach (var project in requested)
        {
            if (_loadFailures.TryGetValue(project.Path, out var failure))
                throw new FuseException(ErrorCode.LoadFailed, $"could not load {_root.Relative(project.Path)}: {failure}");
        }

        var missing = requested.Where(p => !_loaded.ContainsKey(p.Path)).ToList();
        if (missing.Count > 0)
            await LoadAsync(missing, cancellationToken).ConfigureAwait(false);
        if (missing.Count > 0 || _rebuildPending)
        {
            _rebuildPending = false;
            await RebuildAsync(headMoved: false, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Loads <paramref name="project"/> into the loader without touching <see cref="Current"/> or
    ///     <see cref="Baseline"/>, so it can run while requests are served; the next request folds it into both views.
    /// </summary>
    /// <exception cref="FuseException">The project has not been restored or failed to load.</exception>
    public async Task PreloadAsync(ProjectNode project, CancellationToken cancellationToken)
    {
        if (_loaded.ContainsKey(project.Path))
            return;
        await LoadAsync([project], cancellationToken).ConfigureAwait(false);
        _rebuildPending = true;
    }

    private async Task LoadAsync(IReadOnlyList<ProjectNode> missing, CancellationToken cancellationToken)
    {
        var unrestored = missing.SelectMany(Graph.ClosureOf).Where(p => !File.Exists(p.AssetsFile)).Select(p => _root.Relative(p.Path)).Distinct().ToList();
        if (unrestored.Count > 0)
            throw new FuseException(ErrorCode.RestoreNeeded, $"restore needed: run `dotnet restore` ({string.Join(", ", unrestored.Take(3))}{(unrestored.Count > 3 ? ", ..." : "")} not restored)");

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loader = _loader ??= CreateLoader();
            foreach (var project in missing)
            {
                if (_loaded.ContainsKey(project.Path))
                    continue;
                var started = Environment.TickCount64;
                try
                {
                    await loader.OpenProjectAsync(project.Path, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is InvalidOperationException or IOException or ArgumentException)
                {
                    throw new FuseException(ErrorCode.LoadFailed, $"could not load {_root.Relative(project.Path)}: {e.Message}");
                }

                foreach (var loadedProject in loader.CurrentSolution.Projects)
                {
                    if (loadedProject.FilePath is not null)
                        _loaded[loadedProject.FilePath] = 0;
                }

                _log($"loaded {project.Name} in {Environment.TickCount64 - started} ms ({loader.CurrentSolution.ProjectIds.Count} projects open)");
            }

            // A load failure (a missing project reference, an unresolvable SDK) leaves a project that compiles
            // differently from the real build, so it is reported rather than checked.
            foreach (var project in missing)
            {
                if (_loadFailures.TryGetValue(project.Path, out var failure))
                    throw new FuseException(ErrorCode.LoadFailed, $"could not load {_root.Relative(project.Path)}: {failure}");
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>True when <paramref name="node"/> is loaded.</summary>
    public bool IsLoaded(ProjectNode node) => _loaded.ContainsKey(node.Path);

    /// <summary>The loaded Roslyn projects (one per target framework) built from <paramref name="node"/>.</summary>
    public static IEnumerable<Project> ProjectsFor(Solution solution, ProjectNode node) =>
        solution.Projects.Where(p => ChangeTracker.PathComparer.Equals(p.FilePath, node.Path));

    /// <summary>Returns the file's HEAD content as source text, or null when the file is new.</summary>
    public SourceText? HeadText(string path)
    {
        var bytes = Tracker.ReadHead(path);
        return bytes is null ? null : Decode(bytes);
    }

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        Graph = await RepoGraph.EvaluateAsync(_root, cancellationToken).ConfigureAwait(false);
        _log($"evaluated {Graph.Projects.Count} projects in {Environment.TickCount64 - started} ms ({Graph.Failures.Count} failed)");
        foreach (var failure in Graph.Failures)
            _log($"evaluation failed: {failure}");
        _log("projects: " + string.Join(", ", Graph.Projects.Select(p => $"{p.Name} ({p.Sources.Count} sources{(p.IsTest ? ", tests" : "")}{(p.IsExecutable ? ", app" : "")})")));
        await ResetLoaderAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ResetLoaderAsync(CancellationToken cancellationToken)
    {
        // Wait out a background load, so the loader is never disposed under it.
        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _loader?.Dispose();
            _loader = null;
            _loaded.Clear();
            _loadFailures.Clear();
            _rebuildPending = false;
            Current = new AdhocWorkspace().CurrentSolution;
            Baseline = Current;
            BaselineGeneration++;
            LoaderGeneration++;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private MSBuildWorkspace CreateLoader()
    {
        var loader = MSBuildWorkspace.Create();
        loader.LoadMetadataForReferencedProjects = false;
        loader.SkipUnrecognizedProjects = true;
        loader.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind != WorkspaceDiagnosticKind.Failure)
                return;
            _log($"workspace: {e.Diagnostic.Message}");
            var path = Graph.Projects.FirstOrDefault(p => e.Diagnostic.Message.Contains(p.Path, StringComparison.OrdinalIgnoreCase))?.Path;
            if (path is not null)
                _loadFailures[path] = e.Diagnostic.Message;
        });
        return loader;
    }

    /// <summary>Derives both views from the loader again, re-applying every known change.</summary>
    /// <param name="headMoved">
    ///     True when HEAD moved. Otherwise the HEAD view's content is unchanged (it always holds HEAD content for the loaded
    ///     projects; a load only adds projects), so diagnostics cached against it stay valid.
    /// </param>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    private async Task RebuildAsync(bool headMoved, CancellationToken cancellationToken)
    {
        if (_loader is null)
            return;
        var current = _loader.CurrentSolution;
        var baseline = current;
        foreach (var path in _touched.Concat(Tracker.Changed).Distinct(ChangeTracker.PathComparer))
        {
            current = await WithDiskContentAsync(current, path, cancellationToken).ConfigureAwait(false);
            baseline = await WithHeadContentAsync(baseline, path, cancellationToken).ConfigureAwait(false);
        }

        Current = current;
        Baseline = baseline;
        if (headMoved)
            BaselineGeneration++;
    }

    private Task<Solution> WithDiskContentAsync(Solution solution, string path, CancellationToken cancellationToken)
    {
        SourceText? text = null;
        try
        {
            if (File.Exists(path))
                text = Decode(File.ReadAllBytes(path));
        }
        catch (IOException)
        {
            return Task.FromResult(solution);
        }

        return WithContentAsync(solution, path, text, cancellationToken);
    }

    private Task<Solution> WithHeadContentAsync(Solution solution, string path, CancellationToken cancellationToken) =>
        WithContentAsync(solution, path, HeadText(path), cancellationToken);

    /// <summary>Makes every document for <paramref name="path"/> hold <paramref name="text"/>, adding or removing documents as needed.</summary>
    /// <remarks>A document that already holds the same content is left alone, so its compilation stays cached.</remarks>
    private async Task<Solution> WithContentAsync(Solution solution, string path, SourceText? text, CancellationToken cancellationToken)
    {
        var ids = solution.GetDocumentIdsWithFilePath(path);
        var isCSharp = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
        if (text is null)
        {
            foreach (var id in ids)
            {
                solution = solution.GetDocument(id) is not null ? solution.RemoveDocument(id)
                    : solution.GetAdditionalDocument(id) is not null ? solution.RemoveAdditionalDocument(id)
                    : solution;
            }

            return solution;
        }

        if (!ids.IsEmpty)
        {
            foreach (var id in ids)
            {
                TextDocument? existing = solution.GetDocument(id) ?? solution.GetAdditionalDocument(id);
                if (existing is null)
                    continue;
                var existingText = await existing.GetTextAsync(cancellationToken).ConfigureAwait(false);
                if (existingText.ContentEquals(text))
                    continue;
                if (solution.GetDocument(id) is not null)
                    solution = solution.WithDocumentText(id, text, PreservationMode.PreserveIdentity);
                else if (solution.GetAdditionalDocument(id) is not null)
                    solution = solution.WithAdditionalDocumentText(id, text, PreservationMode.PreserveIdentity);
            }

            return solution;
        }

        foreach (var owner in Graph.OwnersOf(path))
        {
            foreach (var project in solution.Projects.Where(p => ChangeTracker.PathComparer.Equals(p.FilePath, owner.Path)).ToList())
            {
                var id = DocumentId.CreateNewId(project.Id, path);
                var folders = Path.GetRelativePath(owner.Directory, Path.GetDirectoryName(path)!)
                    .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                    .Where(f => f != ".")
                    .ToArray();
                solution = isCSharp
                    ? solution.AddDocument(id, Path.GetFileName(path), text, folders, path)
                    : solution.AddAdditionalDocument(id, Path.GetFileName(path), text, folders, path);
            }
        }

        return solution;
    }

    internal static SourceText Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return SourceText.From(stream, Encoding.UTF8, SourceHashAlgorithm.Sha256, throwIfBinaryDetected: false);
    }

    public void Dispose()
    {
        _loader?.Dispose();
        Tracker.Dispose();
    }
}
