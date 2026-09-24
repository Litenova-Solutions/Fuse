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
///     (the same projects with every changed file restored to its HEAD content). Projects load lazily, only when a
///     check or test plan touches them.
/// </summary>
/// <remarks>
///     MSBuildWorkspace is used only as a loader. Its <c>TryApplyChanges</c> writes to disk, so it is never called;
///     both views are immutable <see cref="Solution"/> snapshots derived from the loader's solution and replaced
///     atomically. The baseline only changes when HEAD moves or projects load, so its compilations and the
///     diagnostics computed from them stay cached across checks.
/// </remarks>
internal sealed class RepoWorkspace : IDisposable
{
    private readonly RepoRoot _root;
    private readonly Action<string> _log;
    private readonly HashSet<string> _loaded = new(ChangeTracker.PathComparer);
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

    /// <summary>Increments whenever <see cref="Baseline"/> is rebuilt, which invalidates cached baseline diagnostics.</summary>
    public int BaselineGeneration { get; private set; }

    public RepoRoot Root => _root;

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
            var reopen = _loaded.ToList();
            _touched.UnionWith(batch.SourcePaths);
            if (batch.ProjectFilesChanged)
                await EvaluateAsync(cancellationToken).ConfigureAwait(false);
            else
                ResetLoader();
            // Reopen what was open, so the next check does not pay for a cold load it did not ask for.
            var nodes = reopen.Select(Graph.Find).OfType<ProjectNode>().ToList();
            if (nodes.Count > 0)
                await EnsureLoadedAsync(nodes, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (batch.HeadMoved)
        {
            _touched.UnionWith(batch.SourcePaths);
            await RebuildAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>Loads <paramref name="projects"/> and everything they reference.</summary>
    /// <exception cref="FuseException">A project has not been restored or failed to load.</exception>
    public async Task EnsureLoadedAsync(IEnumerable<ProjectNode> projects, CancellationToken cancellationToken)
    {
        var missing = projects.Where(p => !_loaded.Contains(p.Path)).DistinctBy(p => p.Path).ToList();
        if (missing.Count == 0)
            return;

        var unrestored = missing.SelectMany(Graph.ClosureOf).Where(p => !File.Exists(p.AssetsFile)).Select(p => _root.Relative(p.Path)).Distinct().ToList();
        if (unrestored.Count > 0)
            throw new FuseException(ErrorCode.RestoreNeeded, $"restore needed: run `dotnet restore` ({string.Join(", ", unrestored.Take(3))}{(unrestored.Count > 3 ? ", ..." : "")} not restored)");

        var loader = _loader ??= CreateLoader();
        foreach (var project in missing)
        {
            if (_loaded.Contains(project.Path))
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
                    _loaded.Add(loadedProject.FilePath);
            }

            _log($"loaded {project.Name} in {Environment.TickCount64 - started} ms ({loader.CurrentSolution.ProjectIds.Count} projects open)");
        }

        foreach (var project in missing)
        {
            if (_loadFailures.TryGetValue(project.Path, out var failure) && !loader.CurrentSolution.Projects.Any(p => ChangeTracker.PathComparer.Equals(p.FilePath, project.Path)))
                throw new FuseException(ErrorCode.LoadFailed, $"could not load {_root.Relative(project.Path)}: {failure}");
        }

        await RebuildAsync(cancellationToken).ConfigureAwait(false);
    }

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
        ResetLoader();
    }

    private void ResetLoader()
    {
        _loader?.Dispose();
        _loader = null;
        _loaded.Clear();
        _loadFailures.Clear();
        Current = new AdhocWorkspace().CurrentSolution;
        Baseline = Current;
        BaselineGeneration++;
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

    private async Task RebuildAsync(CancellationToken cancellationToken)
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
