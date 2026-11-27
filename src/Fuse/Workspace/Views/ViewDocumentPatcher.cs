using System.Collections.Concurrent;
using System.Text;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Repo.Tracking;
using Fuse.Workspace.Blueprints;
using Fuse.Workspace.Generations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
namespace Fuse.Workspace.Views;

/// <summary>
///     The document content of the views: reading disk text and HEAD text, patching documents and generated
///     documents into a solution, cloning solutions for transient baselines, and remembering the touched and
///     generated files a rebuild applies again. <see cref="SolutionViews"/> owns the views and calls here for
///     every file it applies.
/// </summary>
internal sealed class ViewDocumentPatcher
{
    private readonly RepoRoot _root;
    private readonly ChangeTracker _tracker;
    private readonly ProjectLoader _projects;
    private readonly HashSet<RepoPath> _touched = [];
    private readonly Lock _touchedLock = new();

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

    public ViewDocumentPatcher(RepoRoot root, ChangeTracker tracker, ProjectLoader projects)
    {
        _root = root;
        _tracker = tracker;
        _projects = projects;
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

    /// <summary>Returns the file's HEAD content as source text, or null when the file is new.</summary>
    internal SourceText? HeadText(RepoPath path)
    {
        var bytes = _tracker.ReadHead(path);
        return bytes is null ? null : Decode(bytes);
    }

    /// <summary>The files of the documents and additional documents <paramref name="current"/> holds under <paramref name="directory"/>.</summary>
    internal IEnumerable<RepoPath> FilesUnder(Solution current, RepoPath directory) =>
        current.Projects.SelectMany(p => p.Documents.Concat<TextDocument>(p.AdditionalDocuments))
            .Select(d => d.FilePath)
            .OfType<string>()
            .Where(directory.Encloses)
            .Select(f => _root.PathOf(f));

    /// <summary>
    ///     Remembers paths whose content may differ from what the loader read, so every later rebuild applies them
    ///     again. A path stays remembered after it matches HEAD again, because the loader may still hold its older
    ///     content.
    /// </summary>
    internal void Touch(IEnumerable<RepoPath> paths)
    {
        lock (_touchedLock)
            _touched.UnionWith(paths);
    }

    /// <summary>
    ///     How many touched paths are churn: touched once but no longer changed from HEAD. A changed path is applied
    ///     again by every rebuild whether or not it is touched, so only churn grows what a rebuild re-applies beyond the
    ///     changed set, and only churn counts against <see cref="SolutionViews.MaxTouchedPaths"/>.
    /// </summary>
    internal int ChurnCount
    {
        get
        {
            lock (_touchedLock)
                return _touched.Count(path => !_tracker.IsChanged(path));
        }
    }

    /// <summary>Empties the touched set. Called only when the projects are evaluated again, which reopens them from disk.</summary>
    internal void ResetTouched()
    {
        lock (_touchedLock)
            _touched.Clear();
    }

    internal List<RepoPath> TouchedAndChangedPaths()
    {
        List<RepoPath> touched;
        lock (_touchedLock)
            touched = [.. _touched];
        return [.. touched.Concat(_tracker.Changed).Distinct()];
    }

    internal Task<Solution> WithDiskContentAsync(Solution solution, RepoPath path, CancellationToken cancellationToken)
    {
        SourceText? text = null;
        try
        {
            if (File.Exists(path.Absolute))
                text = Decode(SharedRead.AllBytes(path.Absolute));
        }
        catch (IOException)
        {
            return Task.FromResult(solution);
        }

        return WithContentAsync(solution, path, text, cancellationToken);
    }

    internal Task<Solution> WithHeadContentAsync(Solution solution, RepoPath path, CancellationToken cancellationToken) =>
        WithContentAsync(solution, path, HeadText(path), cancellationToken);

    /// <summary>Makes every document for <paramref name="path"/> hold <paramref name="text"/>, adding or removing documents as needed.</summary>
    /// <remarks>A document that already holds the same content is left alone, so its compilation stays cached.</remarks>
    internal async Task<Solution> WithContentAsync(Solution solution, RepoPath path, SourceText? text, CancellationToken cancellationToken)
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
                var directory = Path.GetDirectoryName(path.Absolute);
                if (directory is null)
                    throw new InvalidOperationException("A document path has no directory: " + path.Absolute + ".");
                var folders = Path.GetRelativePath(owner.Directory.Absolute, directory)
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

    /// <summary>The documents of <paramref name="solution"/> that lie in a <c>bin</c> or <c>obj</c> folder of their project.</summary>
    internal List<RepoPath> GeneratedFiles(Solution solution)
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

    /// <summary>Remembers the generated files found in <paramref name="solution"/> and returns them.</summary>
    internal List<RepoPath> RefreshGeneratedFiles(Solution solution)
    {
        _generatedFiles = GeneratedFiles(solution);
        return _generatedFiles;
    }

    /// <summary>Empties the generated file stamps and contents, but keeps the touched set.</summary>
    internal void ClearDocumentState()
    {
        _generatedFiles = [];
        _generated.Clear();
        _rewritten.Clear();
    }

    /// <summary>True when generated files were found in the views and their contents are applied on every sync.</summary>
    internal bool HasGeneratedFiles => _generatedFiles.Count > 0;

    /// <summary>
    ///     Applies the rewritten contents kept for the generated files to <paramref name="current"/> and
    ///     <paramref name="baseline"/>, so a view derived again from the loader gets them back without reading
    ///     the files again.
    /// </summary>
    internal async Task<(Solution Current, Solution Baseline)> ApplyRewrittenAsync(Solution current, Solution baseline, CancellationToken cancellationToken)
    {
        foreach (var path in _generatedFiles)
        {
            if (_rewritten.TryGetValue(path, out var text))
            {
                current = await WithContentAsync(current, path, text, cancellationToken).ConfigureAwait(false);
                baseline = await WithContentAsync(baseline, path, text, cancellationToken).ConfigureAwait(false);
            }
        }

        return (current, baseline);
    }

    /// <summary>
    ///     Applies each path's disk content to <paramref name="sourceCurrent"/> and its HEAD content to
    ///     <paramref name="sourceBaseline"/>, remembering each path as touched. The baseline is returned unchanged
    ///     when <paramref name="baselineShed"/> holds.
    /// </summary>
    internal async Task<(Solution Current, Solution Baseline, bool BaselineChanged)> ApplyPatchAsync(Solution sourceCurrent, Solution sourceBaseline, bool baselineShed, IReadOnlyCollection<RepoPath> paths, CancellationToken cancellationToken)
    {
        var current = sourceCurrent;
        var baseline = sourceBaseline;
        foreach (var path in paths)
        {
            lock (_touchedLock)
                _touched.Add(path);
            current = await WithDiskContentAsync(current, path, cancellationToken).ConfigureAwait(false);
            if (!baselineShed)
                baseline = await WithHeadContentAsync(baseline, path, cancellationToken).ConfigureAwait(false);
        }

        return (current, baseline, !baselineShed && !ReferenceEquals(baseline, sourceBaseline));
    }

    /// <summary>
    ///     Applies the content of each generated file that a build wrote since its content was last applied to
    ///     <paramref name="sourceCurrent"/> and <paramref name="sourceBaseline"/>. A file that is missing or being
    ///     written stays as it was, and the next request reads it.
    /// </summary>
    internal async Task<(Solution Current, Solution Baseline, List<(RepoPath Path, SourceText Text)> Applied, bool BaselineChanged)> ApplyGeneratedAsync(Solution sourceCurrent, Solution sourceBaseline, bool baselineShed, CancellationToken cancellationToken)
    {
        var applied = new List<(RepoPath Path, SourceText Text)>();
        if (_generatedFiles.Count == 0)
            return (sourceCurrent, sourceBaseline, applied, false);
        var current = sourceCurrent;
        var baseline = sourceBaseline;
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
                text = Decode(SharedRead.AllBytes(path.Absolute));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            _generated[path] = stamp;
            var previous = current;
            current = await WithContentAsync(current, path, text, cancellationToken).ConfigureAwait(false);
            if (!baselineShed)
                baseline = await WithContentAsync(baseline, path, text, cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(previous, current))
                _rewritten[path] = text;
            applied.Add((path, text));
        }

        return (current, baseline, applied, !baselineShed && !ReferenceEquals(baseline, sourceBaseline));
    }

    internal static Solution CloneUncompiled(Solution source)
    {
        var workspace = new AdhocWorkspace();
        var projects = new List<ProjectInfo>(source.ProjectIds.Count);
        foreach (var project in source.Projects)
            projects.Add(CopyProjectInfoSync(project));
        return workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(), projects: projects));
    }

    internal async Task<Solution> CloneSolutionAsync(Solution source, bool applyHeadTexts, CancellationToken cancellationToken)
    {
        var workspace = new AdhocWorkspace();
        var projects = new List<ProjectInfo>(source.ProjectIds.Count);
        foreach (var project in source.Projects)
            projects.Add(await CopyProjectInfoAsync(project, cancellationToken).ConfigureAwait(false));
        var cloned = workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(), projects: projects));
        if (applyHeadTexts)
        {
            foreach (var path in TouchedAndChangedPaths())
                cloned = await WithHeadContentAsync(cloned, path, cancellationToken).ConfigureAwait(false);
        }

        return cloned;
    }

    internal async Task<TransientBaseline> CloneClosureIntoTransientAsync(
        Solution source,
        IReadOnlyCollection<ProjectId> roots,
        bool applyHeadTexts,
        CancellationToken cancellationToken)
    {
        var closure = new HashSet<ProjectId>();
        var queue = new Queue<ProjectId>(roots);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!closure.Add(id) || source.GetProject(id) is not { } project)
                continue;
            foreach (var reference in project.ProjectReferences)
                queue.Enqueue(reference.ProjectId);
        }

        var workspace = new AdhocWorkspace();
        try
        {
            var projects = new List<ProjectInfo>(closure.Count);
            foreach (var id in closure)
            {
                if (source.GetProject(id) is { } project)
                    projects.Add(await CopyProjectInfoAsync(project, cancellationToken).ConfigureAwait(false));
            }

            var cloned = workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(), projects: projects));
            if (applyHeadTexts)
            {
                foreach (var path in TouchedAndChangedPaths())
                    cloned = await WithHeadContentAsync(cloned, path, cancellationToken).ConfigureAwait(false);
            }

            return new TransientBaseline(workspace, cloned);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Builds a transient HEAD compilation view for <paramref name="projects"/> and their transitive dependencies
    ///     directly from git HEAD blobs so a BaselineStore miss can compile on demand and release 100% of baseline
    ///     Roslyn compilations on disposal.
    /// </summary>
    internal async Task<TransientBaseline> BuildTransientBaselineAsync(Solution? sourceCurrent, IReadOnlyCollection<ProjectNode> projects, CancellationToken cancellationToken)
    {
        if (_projects.Graph is { } graph && _projects.ActiveBlueprint is { } blueprint && _projects.IsLoadedFromBlueprint)
        {
            var closure = projects.SelectMany(graph.ClosureOf).DistinctBy(p => p.Path).ToList();
            if (closure.Count > 0 && blueprint.Covers(_root, closure, allowZeroRestore: _projects.IsUsingZeroRestoreBlueprint))
            {
                var transient = new BlueprintWorkspace(_projects.ResourceCache);
                try
                {
                    var changedSet = new HashSet<RepoPath>(_tracker.Changed);
                    transient.LoadProjects(_root, graph, blueprint, closure, _tracker.Head, changedSet);
                    var solution = transient.Solution;
                    foreach (var path in TouchedAndChangedPaths())
                        solution = await WithHeadContentAsync(solution, path, cancellationToken).ConfigureAwait(false);
                    return new TransientBaseline(transient, solution);
                }
                catch
                {
                    transient.Dispose();
                    throw;
                }
            }
        }

        if (sourceCurrent is { } source && _projects.Graph is { } activeGraph)
        {
            var projectIds = projects
                .SelectMany(activeGraph.ClosureOf)
                .SelectMany(node => RepoWorkspace.ProjectsFor(source, node))
                .Select(p => p.Id)
                .Distinct()
                .ToList();
            return await CloneClosureIntoTransientAsync(source, projectIds, applyHeadTexts: true, cancellationToken).ConfigureAwait(false);
        }

        var empty = new AdhocWorkspace();
        return new TransientBaseline(empty, empty.CurrentSolution);
    }

    private static ProjectInfo CopyProjectInfoSync(Project project) =>
        CreateProjectInfo(
            project,
            CopyTextDocumentsSync(project.Documents, isSource: true),
            CopyTextDocumentsSync(project.AdditionalDocuments, isSource: false),
            CopyTextDocumentsSync(project.AnalyzerConfigDocuments, isSource: false));

    private static async Task<ProjectInfo> CopyProjectInfoAsync(Project project, CancellationToken cancellationToken) =>
        CreateProjectInfo(
            project,
            await CopyTextDocumentsAsync(project.Documents, isSource: true, cancellationToken).ConfigureAwait(false),
            await CopyTextDocumentsAsync(project.AdditionalDocuments, isSource: false, cancellationToken).ConfigureAwait(false),
            await CopyTextDocumentsAsync(project.AnalyzerConfigDocuments, isSource: false, cancellationToken).ConfigureAwait(false));

    private static List<DocumentInfo> CopyTextDocumentsSync(IEnumerable<TextDocument> documents, bool isSource)
    {
        var result = new List<DocumentInfo>();
        foreach (var doc in documents)
        {
            var text = doc.TryGetText(out var loaded) ? loaded : doc.GetTextAsync().GetAwaiter().GetResult();
            var version = doc.GetTextVersionAsync().GetAwaiter().GetResult();
            result.Add(ToDocumentInfo(doc, isSource && doc is Document source ? source.SourceCodeKind : SourceCodeKind.Regular, text, version));
        }

        return result;
    }

    private static async Task<List<DocumentInfo>> CopyTextDocumentsAsync(IEnumerable<TextDocument> documents, bool isSource, CancellationToken cancellationToken)
    {
        var result = new List<DocumentInfo>();
        foreach (var doc in documents)
        {
            var text = doc.TryGetText(out var loaded) ? loaded : await doc.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var version = await doc.GetTextVersionAsync(cancellationToken).ConfigureAwait(false);
            result.Add(ToDocumentInfo(doc, isSource && doc is Document source ? source.SourceCodeKind : SourceCodeKind.Regular, text, version));
        }

        return result;
    }

    private static DocumentInfo ToDocumentInfo(TextDocument doc, SourceCodeKind kind, SourceText text, VersionStamp version) =>
        DocumentInfo.Create(
            doc.Id,
            doc.Name,
            doc.Folders,
            kind,
            TextLoader.From(TextAndVersion.Create(text, version, doc.FilePath)),
            doc.FilePath);

    private static ProjectInfo CreateProjectInfo(
        Project project,
        List<DocumentInfo> documents,
        List<DocumentInfo> additional,
        List<DocumentInfo> analyzerConfigs) =>
        ProjectInfo.Create(
                project.Id,
                project.Version,
                project.Name,
                project.AssemblyName,
                project.Language,
                filePath: project.FilePath,
                outputFilePath: project.OutputFilePath,
                compilationOptions: project.CompilationOptions,
                parseOptions: project.ParseOptions,
                documents: documents,
                projectReferences: project.ProjectReferences,
                metadataReferences: project.MetadataReferences,
                analyzerReferences: project.AnalyzerReferences,
                additionalDocuments: additional)
            .WithOutputRefFilePath(project.OutputRefFilePath)
            .WithAnalyzerConfigDocuments(analyzerConfigs);
}
