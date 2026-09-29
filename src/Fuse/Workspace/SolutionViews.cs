using System.Text;
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
///     stay cached across checks.
/// </remarks>
internal sealed class SolutionViews
{
    private readonly ChangeTracker _tracker;
    private readonly ProjectLoader _projects;
    private readonly AnalyzerShadow _analyzers;
    private readonly HashSet<string> _touched = new(PathRules.PathComparer);

    public SolutionViews(RepoRoot root, ChangeTracker tracker, ProjectLoader projects)
    {
        _tracker = tracker;
        _projects = projects;
        _analyzers = new AnalyzerShadow(root);
    }

    /// <summary>The loaded projects as they are on disk.</summary>
    public Solution Current { get; private set; } = null!;

    /// <summary>The loaded projects with every changed file at its HEAD content.</summary>
    public Solution Baseline { get; private set; } = null!;

    /// <summary>Increments whenever the HEAD view's content changes (HEAD moved, projects reloaded), which invalidates everything cached against it.</summary>
    public int BaselineGeneration { get; private set; }

    /// <summary>Empties both views, after the loader closed every project, and increments <see cref="BaselineGeneration"/>.</summary>
    public void Clear()
    {
        Current = new AdhocWorkspace().CurrentSolution;
        Baseline = Current;
        BaselineGeneration++;
    }

    /// <summary>
    ///     Remembers paths whose content may differ from what the loader read, so every later <see cref="RebuildAsync"/>
    ///     applies them again. A path stays remembered after it matches HEAD again, because the loader may still hold its
    ///     older content.
    /// </summary>
    public void Touch(IEnumerable<string> paths) => _touched.UnionWith(paths);

    /// <summary>Derives both views from the loader's solution again, re-applying every touched and every changed file.</summary>
    /// <remarks>
    ///     <see cref="BaselineGeneration"/> stays the same: the baseline always holds HEAD content for the loaded projects,
    ///     and a load only adds projects, so diagnostics cached against it stay valid. When HEAD moves, the projects are
    ///     evaluated again, which clears both views first.
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
        foreach (var path in _touched.Concat(_tracker.Changed).Distinct(PathRules.PathComparer))
        {
            current = await WithDiskContentAsync(current, path, cancellationToken).ConfigureAwait(false);
            baseline = await WithHeadContentAsync(baseline, path, cancellationToken).ConfigureAwait(false);
        }

        Current = current;
        Baseline = baseline;
    }

    /// <summary>
    ///     Applies each path's disk content to <see cref="Current"/> and its HEAD content to <see cref="Baseline"/>, and
    ///     increments <see cref="BaselineGeneration"/> when the baseline changed. Nothing is applied while no project is
    ///     open, because the loader reads every file from disk when it opens its project.
    /// </summary>
    /// <param name="paths">Absolute paths of the files to apply; each is also touched.</param>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    public async Task PatchAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        if (paths.Count == 0 || _projects.Solution is null)
            return;
        var current = Current;
        var baseline = Baseline;
        foreach (var path in paths)
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

    /// <summary>The paths of the documents and additional documents <see cref="Current"/> holds under <paramref name="directory"/>.</summary>
    public IEnumerable<string> FilesUnder(string directory)
    {
        var prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Current.Projects.SelectMany(p => p.Documents.Concat<TextDocument>(p.AdditionalDocuments))
            .Select(d => d.FilePath)
            .OfType<string>()
            .Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns the file's HEAD content as source text, or null when the file is new.</summary>
    public SourceText? HeadText(string path)
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

        foreach (var owner in _projects.Graph.OwnersOf(path))
        {
            foreach (var project in solution.Projects.Where(p => PathRules.PathComparer.Equals(p.FilePath, owner.Path)).ToList())
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
}
