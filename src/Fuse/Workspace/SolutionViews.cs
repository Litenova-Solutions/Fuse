using System.Collections.Concurrent;
using System.Text;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Repo;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Fuse.Workspace;

/// <summary>
///     The two views of the loaded projects: <see cref="Current"/> (the working tree) and <see cref="Baseline"/> (the
///     same projects with every changed source restored to its HEAD content; project files are evaluated as they are on
///     disk). Both are derived from the loader's solution, and a file's content is applied to them one path at a time.
/// </summary>
/// <remarks>
///     Both views are immutable <see cref="Solution"/> snapshots, replaced whole, so a request reads a pair that belongs
///     together. The baseline's content only changes when HEAD moves or projects reload, so diagnostics computed from it
///     stay cached across checks. In each view a reference to a project outside the change cone (the projects that own
///     changed files, and the dependents a declaration change reaches) is supplied by that project's summary when one
///     is kept (<see cref="SummaryCache"/>), so binding a project in the cone does not build the compilation of every
///     project it references. The referenced project stays in the view with its documents, so reach, coverage and
///     test selection keep reading its documents; a project with no summary kept binds from source exactly as it
///     would without one.
/// </remarks>
internal sealed class SolutionViews
{
    private readonly RepoRoot _root;
    private readonly ChangeTracker _tracker;
    private readonly ProjectLoader _projects;
    private readonly AnalyzerShadow _analyzers;
    private readonly SummaryCache _summaries;
    private readonly HashSet<RepoPath> _touched = [];
    private readonly Lock _touchedLock = new();

    /// <summary>The views with some files at HEAD content that are kept for sessions, least recently derived last. <see cref="ClearSessionViews"/> sheds them.</summary>
    private readonly List<(Solution Source, string Paths, Solution View)> _sessionViews = [];

    /// <summary>Guards <see cref="_sessionViews"/>, which checks that run at the same time derive and shed.</summary>
    private readonly Lock _sessionViewsLock = new();

    /// <summary>The most session views kept; past it the view derived least recently goes, and a later session derives it again from source.</summary>
    internal const int MaxSessionViews = 8;

    /// <summary>The summary each baseline project was last supplied by, for the version of that project it was supplied for, so a view derived again does not key or emit it again.</summary>
    private readonly Dictionary<ProjectId, (VersionStamp Version, MetadataReference? Reference)> _summaryReferences = [];

    /// <summary>Guards <see cref="_summaryReferences"/>.</summary>
    private readonly Lock _summaryLock = new();

    /// <summary>The source view the kept summary view of <see cref="Current"/> was derived from, so a round that changed neither view reuses it.</summary>
    private Solution? _summarySourceCurrent;

    /// <summary>The source view the kept summary view of <see cref="Baseline"/> was derived from.</summary>
    private Solution? _summarySourceBaseline;

    /// <summary><see cref="Current"/> with summaries outside the change cone, or null when it is the source view itself.</summary>
    private Solution? _summaryCurrent;

    /// <summary><see cref="Baseline"/> with summaries outside the change cone, or null when it is the source view itself.</summary>
    private Solution? _summaryBaseline;

    /// <summary>Whether the kept summary views were derived with summaries, as <see cref="UseSummaries"/> was when they were derived.</summary>
    private bool _summaryApplied;

    /// <summary>The working tree before summaries: every referenced project as a live compilation, which reach and test emit read.</summary>
    private Solution? _sourceCurrent;

    /// <summary>The baseline before summaries.</summary>
    private Solution? _sourceBaseline;

    /// <summary>The most paths the touched set holds. Past it the next sync re-evaluates the projects and resets the set, so it stays bounded however long the engine runs.</summary>
    internal const int MaxTouchedPaths = 2000;

    /// <summary>
    ///     Each generated file of the views (<see cref="GeneratedFiles"/>), with the length and last write time it had when
    ///     its content was last applied to both views.
    /// </summary>
    private readonly ConcurrentDictionary<RepoPath, (long Length, DateTime WrittenUtc)> _generated = new();

    /// <summary>
    ///     The documents of the views that lie in a <c>bin</c> or <c>obj</c> folder of their project: files a build
    ///     generates, such as an API client or <c>GlobalUsings.g.cs</c>. They are found again whenever the views are
    ///     derived.
    /// </summary>
    private List<RepoPath> _generatedFiles = [];

    /// <summary>
    ///     The content of each generated file that differed from what the loader read for it, as last applied, so a view
    ///     derived again from the loader gets it back without reading the file again.
    /// </summary>
    private readonly ConcurrentDictionary<RepoPath, SourceText> _rewritten = new();

    /// <summary>
    ///     The loader's solution both views were last derived from, or null after <see cref="Clear"/>. It is set only
    ///     once a <see cref="RebuildAsync"/> has finished, so a rebuild that was cut short is done again.
    /// </summary>
    private Solution? _derivedFrom;

    /// <summary>
    ///     The HEAD the baseline's content was read at, by the last <see cref="Clear"/> or <see cref="RebuildAsync"/>. A
    ///     rebuild at another HEAD reads other content for every changed file, so it increments
    ///     <see cref="BaselineGeneration"/>.
    /// </summary>
    private string? _baselineHead;

    public SolutionViews(RepoRoot root, ChangeTracker tracker, ProjectLoader projects, Action<string>? log = null)
    {
        _root = root;
        _tracker = tracker;
        _projects = projects;
        _analyzers = new AnalyzerShadow(root);
        _summaries = new SummaryCache(root, log ?? (_ => { }));
    }

    /// <summary>The summaries the views supply references from, in the shared directory.</summary>
    public SummaryCache Summaries => _summaries;

    /// <summary>
    ///     Whether the views supply references to projects outside the change cone from summaries, which is how a check
    ///     binds. It is set once, before the first check, only by a caller that compares the two answers against each
    ///     other (the differential tests in Fuse.Tests); a check with it false binds from source everywhere, and no
    ///     surface reads it, so it is not a setting.
    /// </summary>
    public bool UseSummaries { get; set; } = true;

    /// <summary>The loaded projects as they are on disk, with references to projects outside the change cone supplied by their summaries when one is kept.</summary>
    public Solution Current => _summaryCurrent ?? _sourceCurrent!;

    /// <summary>The loaded projects with every changed file at its HEAD content, with references to projects outside the change cone supplied by their summaries when one is kept.</summary>
    public Solution Baseline => _summaryBaseline ?? _sourceBaseline!;

    /// <summary>Increments whenever the baseline's content changes (HEAD moved, projects reloaded), which invalidates everything cached against it.</summary>
    public int BaselineGeneration { get; private set; }

    /// <summary>
    ///     True when both views were derived from the loader's solution as it is now, so they hold every project the
    ///     loader has open. A project a background load opened, or one a load opened before its request was cancelled,
    ///     is in neither view until the next <see cref="RebuildAsync"/>.
    /// </summary>
    /// <remarks>
    ///     The loader's solution is an immutable snapshot that the loader replaces when it opens a project, and Fuse never
    ///     applies changes to it, so comparing the instance is enough. A background load publishes a project in
    ///     <see cref="ProjectLoader.IsLoaded"/> after the loader's solution holds it, so a request that finds the project
    ///     loaded also finds this false until it rebuilds.
    /// </remarks>
    public bool HoldEveryOpenProject => _projects.Solution is not { } loaded || ReferenceEquals(loaded, _derivedFrom);

    /// <summary>Empties both views, after the loader closed every project, and increments <see cref="BaselineGeneration"/>.</summary>
    public void Clear()
    {
        _sourceCurrent = new AdhocWorkspace().CurrentSolution;
        _sourceBaseline = _sourceCurrent;
        _summaryCurrent = _sourceCurrent;
        _summaryBaseline = _sourceBaseline;
        _summarySourceCurrent = _sourceCurrent;
        _summarySourceBaseline = _sourceBaseline;
        _derivedFrom = null;
        _generatedFiles = [];
        _generated.Clear();
        _rewritten.Clear();
        lock (_sessionViewsLock)
            _sessionViews.Clear();
        lock (_summaryLock)
            _summaryReferences.Clear();
        _baselineHead = _tracker.Head;
        BaselineGeneration++;
    }

    /// <summary>
    ///     Sheds the session views: the views with some files at HEAD content that a session's check is answered from.
    ///     A later check of that session derives its view from the current view again, as a check did before its view
    ///     was kept, and answers the same.
    /// </summary>
    public void ClearSessionViews()
    {
        lock (_sessionViewsLock)
            _sessionViews.Clear();
    }

    /// <summary>
    ///     Sheds the HEAD compilations by dropping the baseline view, so every compilation built over HEAD can go.
    ///     The working tree view stays. The next sync derives both views from the loader again, because the views no
    ///     longer hold what it has open, and reads HEAD's errors from the stored results in the meantime. The
    ///     baseline's content is the same when it is derived again, so <see cref="BaselineGeneration"/> stays.
    /// </summary>
    public void ClearHeadCompilations()
    {
        _sourceBaseline = new AdhocWorkspace().CurrentSolution;
        _summaryBaseline = _sourceBaseline;
        _summarySourceBaseline = _sourceBaseline;
        _derivedFrom = null;
        _generatedFiles = [];
        _baselineHead = _tracker.Head;
    }

    /// <summary>
    ///     Sheds the idle cone projects: the projects in neither view after this call are the dependents a changed
    ///     file reaches and every other loaded project, except the projects that own changed files and everything
    ///     they reference, which stay. The loader keeps them open, and the next sync derives both views from it
    ///     again, so a later request that reaches a shed project binds it from source as it would have anyway; what
    ///     goes is only the compilations binding it had built.
    /// </summary>
    public void ClearIdleConeProjects()
    {
        if (_sourceCurrent is null || _sourceBaseline is null || _projects.Graph is not { } graph)
            return;
        var keep = new HashSet<RepoPath>();
        foreach (var path in _tracker.Changed)
        {
            foreach (var owner in graph.OwnersOf(path))
            {
                foreach (var node in graph.ClosureOf(owner))
                    keep.Add(node.Path);
            }
        }

        var current = _sourceCurrent;
        var baseline = _sourceBaseline;
        var shed = false;
        foreach (var project in _sourceCurrent.Projects.ToList())
        {
            if (project.FilePath is not { } file || graph.Find(_root.PathOf(file)) is not { } node || keep.Contains(node.Path))
                continue;
            current = current.RemoveProject(project.Id);
            if (baseline.GetProject(project.Id) is not null)
                baseline = baseline.RemoveProject(project.Id);
            shed = true;
        }

        if (!shed)
            return;
        _sourceCurrent = current;
        _sourceBaseline = baseline;
        _derivedFrom = null;
        ClearSessionViews();
        RefreshSummaryViewsFromCache();
    }

    /// <summary>The project files of the change cone: the owners of the changed files, and every dependent of an owner.</summary>
    private HashSet<RepoPath> ConePaths()
    {
        var cone = new HashSet<RepoPath>();
        if (_projects.Graph is not { } graph)
            return cone;
        var queue = new Queue<ProjectNode>();
        foreach (var path in _tracker.Changed)
        {
            foreach (var owner in graph.OwnersOf(path))
            {
                if (cone.Add(owner.Path))
                    queue.Enqueue(owner);
            }
        }

        while (queue.Count > 0)
        {
            foreach (var dependent in graph.DependentsOf(queue.Dequeue()))
            {
                if (cone.Add(dependent.Path))
                    queue.Enqueue(dependent);
            }
        }

        return cone;
    }

    /// <summary>
    ///     <paramref name="source"/> with each reference to a project outside <paramref name="cone"/> supplied by the
    ///     summary <paramref name="summaryFor"/> gives for that project at HEAD, when it gives one. A reference with
    ///     an alias or an embedded interop type, a reference to a project the graph or the baseline does not hold,
    ///     and a project with no summary kept all stay as they are: a doubt falls back to source.
    /// </summary>
    private Solution WithSummaries(Solution source, Solution baseline, IReadOnlySet<RepoPath> cone, Func<Project, MetadataReference?> summaryFor)
    {
        if (_projects.Graph is not { } graph)
            return source;
        var result = source;
        foreach (var project in source.Projects)
        {
            List<ProjectReference>? kept = null;
            List<MetadataReference>? added = null;
            foreach (var reference in project.ProjectReferences)
            {
                if (reference.ProjectId == project.Id || !reference.Properties.Aliases.IsEmpty || reference.Properties.EmbedInteropTypes)
                    continue;
                if (source.GetProject(reference.ProjectId)?.FilePath is not { } referencedFile)
                    continue;
                if (graph.Find(_root.PathOf(referencedFile)) is not { } node || cone.Contains(node.Path))
                    continue;
                if (baseline.GetProject(reference.ProjectId) is not { } baselineProject)
                    continue;
                if (summaryFor(baselineProject) is not { } summary)
                    continue;
                kept ??= [.. project.ProjectReferences];
                added ??= [];
                kept.Remove(reference);
                added.Add(summary);
            }

            if (kept is not null && added is { Count: > 0 })
                result = result.WithProjectReferences(project.Id, kept).WithProjectMetadataReferences(project.Id, project.MetadataReferences.Concat(added));
        }

        return result;
    }

    /// <summary>The summary kept for <paramref name="baselineProject"/> at the version it is now, or null when none was kept for it.</summary>
    private MetadataReference? CachedSummary(Project baselineProject)
    {
        lock (_summaryLock)
            return _summaryReferences.TryGetValue(baselineProject.Id, out var entry) && entry.Version == baselineProject.Version ? entry.Reference : null;
    }

    /// <summary>Derives both summary views from the source views again, with only the summaries already kept, and emits nothing.</summary>
    private void RefreshSummaryViewsFromCache()
    {
        if (_sourceCurrent is null || _sourceBaseline is null)
            return;
        _summaryApplied = UseSummaries;
        if (!UseSummaries)
        {
            _summaryCurrent = _sourceCurrent;
            _summaryBaseline = _sourceBaseline;
            _summarySourceCurrent = _sourceCurrent;
            _summarySourceBaseline = _sourceBaseline;
            return;
        }

        var cone = ConePaths();
        _summaryCurrent = WithSummaries(_sourceCurrent, _sourceBaseline, cone, CachedSummary);
        _summaryBaseline = WithSummaries(_sourceBaseline, _sourceBaseline, cone, CachedSummary);
        _summarySourceCurrent = _sourceCurrent;
        _summarySourceBaseline = _sourceBaseline;
    }

    /// <summary>
    ///     Derives both summary views from the source views again, emitting the summary of each outside project that
    ///     has none kept for its version. A round that changed neither source view reuses the views it derived last.
    /// </summary>
    private async Task RefreshSummaryViewsAsync(CancellationToken cancellationToken)
    {
        if (_sourceCurrent is null || _sourceBaseline is null)
            return;
        if (ReferenceEquals(_sourceCurrent, _summarySourceCurrent) && ReferenceEquals(_sourceBaseline, _summarySourceBaseline) && _summaryCurrent is not null && _summaryApplied == UseSummaries)
            return;
        if (UseSummaries && _projects.Graph is { } graph)
        {
            var cone = ConePaths();
            var needed = new HashSet<ProjectId>();
            foreach (var project in _sourceCurrent.Projects.Concat(_sourceBaseline.Projects))
            {
                foreach (var reference in project.ProjectReferences)
                {
                    if (_sourceBaseline.GetProject(reference.ProjectId) is not { } baselineProject || baselineProject.FilePath is not { } referencedFile)
                        continue;
                    if (graph.Find(_root.PathOf(referencedFile)) is not { } node || cone.Contains(node.Path))
                        continue;
                    if (needed.Add(reference.ProjectId))
                        await EnsureSummaryAsync(baselineProject, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        RefreshSummaryViewsFromCache();
    }

    /// <summary>Keeps the summary of <paramref name="baselineProject"/> for its version, emitted from HEAD when none is kept. A failure to key or emit keeps none, and binds source.</summary>
    private async Task EnsureSummaryAsync(Project baselineProject, CancellationToken cancellationToken)
    {
        if (CachedSummary(baselineProject) is not null)
            return;
        lock (_summaryLock)
        {
            if (_summaryReferences.TryGetValue(baselineProject.Id, out var entry) && entry.Version == baselineProject.Version)
                return;
        }

        MetadataReference? reference = null;
        if (await _summaries.KeyForAsync(_tracker.Head, baselineProject, _projects.Graph, cancellationToken).ConfigureAwait(false) is { } key)
            reference = await _summaries.GetOrEmitAsync(baselineProject, key, cancellationToken).ConfigureAwait(false);
        lock (_summaryLock)
            _summaryReferences[baselineProject.Id] = (baselineProject.Version, reference);
    }

    /// <summary>
    ///     Remembers paths whose content may differ from what the loader read, so every later <see cref="RebuildAsync"/>
    ///     applies them again. A path stays remembered after it matches HEAD again, because the loader may still hold its
    ///     older content.
    /// </summary>
    public void Touch(IEnumerable<RepoPath> paths)
    {
        lock (_touchedLock)
            _touched.UnionWith(paths);
    }

    /// <summary>True when the touched set holds more than <see cref="MaxTouchedPaths"/> paths, so the next sync re-evaluates the projects and resets it.</summary>
    public bool TouchedOverflows
    {
        get
        {
            lock (_touchedLock)
                return _touched.Count > MaxTouchedPaths;
        }
    }

    /// <summary>Empties the touched set. Called only when the projects are evaluated again, which reopens them from disk.</summary>
    public void ResetTouched()
    {
        lock (_touchedLock)
            _touched.Clear();
    }

    /// <summary>Derives both views from the loader's solution again, re-applying every touched and every changed file.</summary>
    /// <remarks>
    ///     At the same HEAD, <see cref="BaselineGeneration"/> stays the same: the baseline always holds HEAD content for
    ///     the loaded projects, and a load only adds projects, so diagnostics cached against it stay valid. A rebuild at a
    ///     HEAD other than the one the baseline was read at increments it, so no diagnostic cached for the earlier HEAD is
    ///     taken for the new one. A moved HEAD normally clears both views first, when the projects are evaluated again.
    /// </remarks>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    public async Task RebuildAsync(CancellationToken cancellationToken)
    {
        var loaded = _projects.Solution;
        if (loaded is null)
            return;
        // The repository's own analyzers load from a copy, so a real build can still overwrite them.
        var current = _analyzers.Apply(loaded);
        var baseline = current;
        var head = _tracker.Head;
        List<RepoPath> touched;
        lock (_touchedLock)
            touched = [.. _touched];
        foreach (var path in touched.Concat(_tracker.Changed).Distinct())
        {
            current = await WithDiskContentAsync(current, path, cancellationToken).ConfigureAwait(false);
            baseline = await WithHeadContentAsync(baseline, path, cancellationToken).ConfigureAwait(false);
        }

        _sourceCurrent = current;
        _sourceBaseline = baseline;
        _derivedFrom = loaded;
        if (!string.Equals(head, _baselineHead, StringComparison.Ordinal))
        {
            _baselineHead = head;
            BaselineGeneration++;
            lock (_summaryLock)
                _summaryReferences.Clear();
        }

        // The loader read the generated files when it opened their projects. A file a build wrote again since then holds
        // the content applied after that build, which the views derived again from the loader no longer have; the others
        // are read once, when they first appear, and only compared by length and write time after that.
        _generatedFiles = GeneratedFiles(current);
        foreach (var path in _generatedFiles)
        {
            if (_rewritten.TryGetValue(path, out var text))
            {
                current = await WithContentAsync(current, path, text, cancellationToken).ConfigureAwait(false);
                baseline = await WithContentAsync(baseline, path, text, cancellationToken).ConfigureAwait(false);
            }
        }

        _sourceCurrent = current;
        _sourceBaseline = baseline;
        await RefreshGeneratedAsync(cancellationToken).ConfigureAwait(false);
        await RefreshSummaryViewsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Applies the content of each generated file that a build wrote since its content was last applied to both views,
    ///     and increments <see cref="BaselineGeneration"/> when the baseline changed. A generated file is not in git, so
    ///     HEAD has no content of its own for it; both views hold the file as the last build wrote it.
    /// </summary>
    /// <remarks>
    ///     The watcher skips <c>bin</c> and <c>obj</c> folders, so these files are compared by length and write time on
    ///     every request instead. A file that is missing or being written stays as it was, and the next request reads it.
    /// </remarks>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    public async Task RefreshGeneratedAsync(CancellationToken cancellationToken)
    {
        if (_generatedFiles.Count == 0)
            return;
        var current = _sourceCurrent!;
        var baseline = _sourceBaseline!;
        foreach (var path in _generatedFiles)
        {
            SourceText text;
            (long, DateTime) stamp;
            try
            {
                var file = new FileInfo(path.Absolute);
                if (!file.Exists)
                    continue;
                stamp = (file.Length, file.LastWriteTimeUtc);
                if (_generated.TryGetValue(path, out var known) && known == stamp)
                    continue;
                text = Decode(File.ReadAllBytes(path.Absolute));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            _generated[path] = stamp;
            var before = current;
            current = await WithContentAsync(current, path, text, cancellationToken).ConfigureAwait(false);
            baseline = await WithContentAsync(baseline, path, text, cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(before, current))
                _rewritten[path] = text;
        }

        _sourceCurrent = current;
        if (!ReferenceEquals(baseline, _sourceBaseline))
        {
            _sourceBaseline = baseline;
            BaselineGeneration++;
        }

        await RefreshSummaryViewsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The documents of <paramref name="solution"/> that lie in a <c>bin</c> or <c>obj</c> folder of their project.</summary>
    private List<RepoPath> GeneratedFiles(Solution solution)
    {
        var result = new HashSet<RepoPath>();
        foreach (var project in solution.Projects)
        {
            if (Path.GetDirectoryName(project.FilePath) is not { } directory)
                continue;
            foreach (var document in project.Documents)
            {
                if (document.FilePath is { } file && Path.IsPathRooted(file) && PathRules.IsBuildOutput(directory, file))
                    result.Add(_root.PathOf(file));
            }
        }

        return [.. result];
    }

    /// <summary>
    ///     Applies each path's disk content to <see cref="Current"/> and its HEAD content to <see cref="Baseline"/>, and
    ///     increments <see cref="BaselineGeneration"/> when the baseline changed. Nothing is applied while no project is
    ///     open, because the loader reads every file from disk when it opens its project.
    /// </summary>
    /// <param name="paths">The files to apply; each is also touched.</param>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    public async Task PatchAsync(IReadOnlyCollection<RepoPath> paths, CancellationToken cancellationToken)
    {
        if (paths.Count == 0 || _projects.Solution is null)
            return;
        var current = _sourceCurrent!;
        var baseline = _sourceBaseline!;
        foreach (var path in paths)
        {
            lock (_touchedLock)
                _touched.Add(path);
            current = await WithDiskContentAsync(current, path, cancellationToken).ConfigureAwait(false);
            baseline = await WithHeadContentAsync(baseline, path, cancellationToken).ConfigureAwait(false);
        }

        _sourceCurrent = current;
        if (!ReferenceEquals(baseline, _sourceBaseline))
        {
            _sourceBaseline = baseline;
            BaselineGeneration++;
        }

        await RefreshSummaryViewsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <see cref="Current"/> with each of <paramref name="paths"/> at its HEAD content, and a file HEAD does not have
    ///     left out. Neither view changes; the result shares every other document with <see cref="Current"/>, so only the
    ///     projects that compile those files bind again. The result is kept as a session view for the same view and paths,
    ///     up to <see cref="MaxSessionViews"/>, and shed by <see cref="ClearSessionViews"/>.
    /// </summary>
    /// <param name="view">The current view to derive from, as a pinned generation holds it.</param>
    /// <param name="paths">The files to put back at HEAD content.</param>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    public async Task<Solution> CurrentWithHeadContentAsync(Solution view, IEnumerable<RepoPath> paths, CancellationToken cancellationToken)
    {
        var listed = paths as IReadOnlyCollection<RepoPath> ?? [.. paths];
        var key = string.Join("\n", listed.Select(p => p.Relative).Order(StringComparer.Ordinal));
        lock (_sessionViewsLock)
        {
            foreach (var (source, pathsKey, kept) in _sessionViews)
            {
                if (ReferenceEquals(source, view) && pathsKey == key)
                    return kept;
            }
        }

        var solution = view;
        foreach (var path in listed)
            solution = await WithHeadContentAsync(solution, path, cancellationToken).ConfigureAwait(false);
        lock (_sessionViewsLock)
        {
            if (!_sessionViews.Any(v => ReferenceEquals(v.Source, view) && v.Paths == key))
            {
                _sessionViews.Add((view, key, solution));
                while (_sessionViews.Count > MaxSessionViews)
                    _sessionViews.RemoveAt(0);
            }
        }

        return solution;
    }

    public async Task<Solution> CurrentWithHeadContentAsync(IEnumerable<RepoPath> paths, CancellationToken cancellationToken) =>
        await CurrentWithHeadContentAsync(Current, paths, cancellationToken).ConfigureAwait(false);

    /// <summary>The files of the documents and additional documents <see cref="Current"/> holds under <paramref name="directory"/>.</summary>
    public IEnumerable<RepoPath> FilesUnder(RepoPath directory) =>
        Current.Projects.SelectMany(p => p.Documents.Concat<TextDocument>(p.AdditionalDocuments))
            .Select(d => d.FilePath)
            .Where(directory.Encloses)
            .Select(f => _root.PathOf(f!));

    /// <summary>Returns the file's HEAD content as source text, or null when the file is new.</summary>
    public SourceText? HeadText(RepoPath path)
    {
        var bytes = _tracker.ReadHead(path);
        return bytes is null ? null : Decode(bytes);
    }

    /// <summary>
    ///     Reads file bytes as source text: UTF-8 unless a byte order mark names another encoding, with a SHA-256
    ///     checksum. A file that looks binary is read as text rather than refused, so one odd file cannot fail a request.
    /// </summary>
    public static SourceText Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return SourceText.From(stream, Encoding.UTF8, SourceHashAlgorithm.Sha256, throwIfBinaryDetected: false);
    }

    private Task<Solution> WithDiskContentAsync(Solution solution, RepoPath path, CancellationToken cancellationToken)
    {
        SourceText? text = null;
        try
        {
            if (File.Exists(path.Absolute))
                text = Decode(File.ReadAllBytes(path.Absolute));
        }
        catch (IOException)
        {
            return Task.FromResult(solution);
        }

        return WithContentAsync(solution, path, text, cancellationToken);
    }

    private Task<Solution> WithHeadContentAsync(Solution solution, RepoPath path, CancellationToken cancellationToken) =>
        WithContentAsync(solution, path, HeadText(path), cancellationToken);

    /// <summary>Makes every document for <paramref name="path"/> hold <paramref name="text"/>, adding or removing documents as needed.</summary>
    /// <remarks>A document that already holds the same content is left alone, so its compilation stays cached.</remarks>
    private async Task<Solution> WithContentAsync(Solution solution, RepoPath path, SourceText? text, CancellationToken cancellationToken)
    {
        var ids = solution.GetDocumentIdsWithFilePath(path.Absolute);
        var isCSharp = path.Absolute.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
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

        foreach (var owner in _projects.Graph.OwnersOf(path))
        {
            foreach (var project in RepoWorkspace.ProjectsFor(solution, owner).ToList())
            {
                var id = DocumentId.CreateNewId(project.Id, path.Absolute);
                var folders = Path.GetRelativePath(owner.Directory.Absolute, Path.GetDirectoryName(path.Absolute)!)
                    .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                    .Where(f => f != ".")
                    .ToArray();
                solution = isCSharp
                    ? solution.AddDocument(id, path.FileName, text, folders, path.Absolute)
                    : solution.AddAdditionalDocument(id, path.FileName, text, folders, path.Absolute);
            }
        }

        return solution;
    }
}
