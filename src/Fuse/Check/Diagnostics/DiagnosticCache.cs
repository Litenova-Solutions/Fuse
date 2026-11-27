using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Fuse.Changes;
using Fuse.Check.Model;
using Fuse.Check.Pipeline;
using Fuse.Paths;
using Microsoft.CodeAnalysis;
namespace Fuse.Check.Diagnostics;

/// <summary>
///     The working-tree and baseline diagnostic caches of one collector: each working-tree file's compiler and
///     per-file analyzer errors under the key they were found under, each baseline file's and project's finished
///     errors by baseline generation, and the declaration fingerprints those keys hash. Baseline results whose
///     analyzers finished stay until the baseline changes; a newer baseline generation drops only older keys.
/// </summary>
internal sealed class DiagnosticCache
{
    private readonly ConcurrentDictionary<(int Generation, RepoPath Path), BaselineFile> _baselineFiles = new();
    private readonly ConcurrentDictionary<(int Generation, ProjectId Project), IReadOnlyList<CompilerError>> _baselineProjects = new();
    private readonly CompilationAnalyses _analyses;
    private readonly Lock _baselineLock = new();
    private volatile int _cachedGeneration = -1;
    private readonly Func<int> _configurationGeneration;

    /// <summary>The compiler and per-file analyzer errors of each working-tree file at the last check that bound it, by the key they were found under.</summary>
    private readonly ConcurrentDictionary<DocumentId, CachedFile> _currentFiles = new();

    /// <summary>The most projects whose declaration fingerprints are kept; past it they are read again.</summary>
    private const int MaxDeclarationFingerprints = 512;

    /// <summary>
    ///     The declaration fingerprint of a project, by the project's state: its semantic version, the version of
    ///     its most recently changed document, its additional documents' text versions and its document counts.
    ///     A project in that state again is not read again for every file of a check, so a body edit, which moves
    ///     only its own project's state, costs at most that one project's fingerprint.
    /// </summary>
    private readonly ConcurrentDictionary<FingerprintKey, string> _declarationFingerprints = new();

    /// <summary>How many declaration fingerprints were computed, counted when a project's declarations are read; a test reads it to see that a state is computed once.</summary>
    private int _fingerprintComputations;

    /// <summary>How many declaration fingerprints have been computed, as against read back from the state cache above.</summary>
    internal int DeclarationFingerprintComputations => Volatile.Read(ref _fingerprintComputations);

    private readonly Lock _configurationLock = new();
    private int _currentFilesGeneration = -1;

    /// <param name="configurationGeneration">Changes whenever project configuration may have changed.</param>
    /// <param name="analyses">The shared compilation-wide analyzer runs, stopped when a cache they belong to goes.</param>
    public DiagnosticCache(Func<int> configurationGeneration, CompilationAnalyses analyses)
    {
        _configurationGeneration = configurationGeneration;
        _analyses = analyses;
    }

    /// <summary>
    ///     Prepares the shared caches for <paramref name="context"/>'s configuration. Projects that loaded again have
    ///     new document ids, so what was kept for the old ones is dropped with them once, however many checks at the
    ///     same time see the new configuration, and the analyzer runs over their old compilations stop when no
    ///     pinned check waits on them.
    /// </summary>
    internal void BeginConfiguration(CheckContext context)
    {
        _ = context;
        var generation = _configurationGeneration();
        lock (_configurationLock)
        {
            if (generation == _currentFilesGeneration)
                return;
            _currentFiles.Clear();
            _declarationFingerprints.Clear();
            _currentFilesGeneration = generation;
            _analyses.StopAll(baseline: false);
            _analyses.StopAll(baseline: true);
        }
    }

    /// <summary>
    ///     Drops the baseline results of generations older than <paramref name="generation"/>, and stops the analyzer
    ///     runs over the baseline when no pinned check waits on them. A generation older than the cached one is a warm
    ///     that started before HEAD moved again, which must not drop what a newer baseline already holds or stop its
    ///     runs. Results are keyed by their generation, so a check pinned to an older baseline computes its own again
    ///     after a drop instead of taking the newer baseline's.
    /// </summary>
    internal void ForgetOlderBaseline(int generation)
    {
        if (generation <= _cachedGeneration)
            return;
        lock (_baselineLock)
        {
            if (generation <= _cachedGeneration)
                return;
            foreach (var key in _baselineFiles.Keys.Where(k => k.Generation < generation).ToList())
                _baselineFiles.TryRemove(key, out _);
            foreach (var key in _baselineProjects.Keys.Where(k => k.Generation < generation).ToList())
                _baselineProjects.TryRemove(key, out _);
            _analyses.StopAll(baseline: true);
            _cachedGeneration = generation;
        }
    }

    /// <summary>True when <paramref name="generation"/> is the cached baseline generation.</summary>
    internal bool IsCachedGeneration(int generation) => generation == _cachedGeneration;

    /// <summary>
    ///     Releases what a fill or a check built over the baseline of one project: its kept baseline results go, and
    ///     its analyzer runs stop when no pinned check waits on them. The stored results stay, so the next check
    ///     reads them from the store instead of binding HEAD again.
    /// </summary>
    internal void ReleaseBaselineProject(ProjectId project, IReadOnlyList<RepoPath> paths, int generation)
    {
        _baselineProjects.TryRemove((generation, project), out _);
        foreach (var path in paths)
            _baselineFiles.TryRemove((generation, path), out _);
        _analyses.Release(project, baseline: true);
    }

    /// <summary>
    ///     Releases the kept baseline results and the baseline analyzer runs, when the check that built them ends.
    ///     Runs a pinned check waits on stop when its last pin goes (<see cref="CompilationAnalyses"/>). The stored
    ///     results stay, so a later check reads HEAD's errors from the store.
    /// </summary>
    internal void ReleaseBaseline()
    {
        _baselineFiles.Clear();
        _baselineProjects.Clear();
        _analyses.StopAll(baseline: true);
    }

    /// <summary>True when the baseline file result for <paramref name="generation"/> and <paramref name="path"/> is kept and covers <paramref name="compilationWide"/>.</summary>
    internal bool TryGetBaselineFile(int generation, RepoPath path, bool compilationWide, out IReadOnlyList<CompilerError> errors)
    {
        if (_baselineFiles.TryGetValue((generation, path), out var cached) && (cached.CompilationWide || !compilationWide))
        {
            errors = cached.Errors;
            return true;
        }

        errors = [];
        return false;
    }

    /// <summary>Keeps the baseline file result for <paramref name="generation"/> and <paramref name="path"/>.</summary>
    internal void StoreBaselineFile(int generation, RepoPath path, IReadOnlyList<CompilerError> errors, bool compilationWide) =>
        _baselineFiles[(generation, path)] = new BaselineFile(errors, compilationWide);

    /// <summary>True when the baseline project result for <paramref name="generation"/> and <paramref name="project"/> is kept.</summary>
    internal bool TryGetBaselineProject(int generation, ProjectId project, out IReadOnlyList<CompilerError> errors)
    {
        if (_baselineProjects.TryGetValue((generation, project), out var cached))
        {
            errors = cached;
            return true;
        }

        errors = [];
        return false;
    }

    /// <summary>Keeps the baseline project result for <paramref name="generation"/> and <paramref name="project"/>.</summary>
    internal void StoreBaselineProject(int generation, ProjectId project, IReadOnlyList<CompilerError> errors) =>
        _baselineProjects[(generation, project)] = errors;

    /// <summary>True when the working-tree file <paramref name="id"/> is kept under <paramref name="key"/>.</summary>
    internal bool TryGetCurrentFile(DocumentId id, FileKey key, out IReadOnlyList<CompilerError> errors)
    {
        if (_currentFiles.TryGetValue(id, out var cached) && cached.Key == key)
        {
            errors = cached.Errors;
            return true;
        }

        errors = [];
        return false;
    }

    /// <summary>Keeps the working-tree file <paramref name="id"/>'s errors under <paramref name="key"/>.</summary>
    internal void StoreCurrentFile(DocumentId id, FileKey key, IReadOnlyList<CompilerError> errors) =>
        _currentFiles[id] = new CachedFile(key, errors);

    /// <summary>
    ///     What a working-tree file's compiler and per-file analyzer errors depend on: its text, the declarations of
    ///     every project in the solution, and the project configuration. Each project's fingerprint is computed
    ///     once for its state (<see cref="DeclarationFingerprintAsync"/>), so this reads only Roslyn's versions for
    ///     the projects whose state an earlier file already fingerprinted. Every project in the solution counts, not
    ///     only the ones this file's project references as projects. The declarations are read from syntax, as a
    ///     fingerprint of each declaration's identity, surface and parameter lists, because a version taken from
    ///     Roslyn changes when a reference outside the change cone stops being supplied by a summary and becomes
    ///     source again, although no declaration changed: that switch forks the project, and its semantic version
    ///     is the newer of its declarations' version and the forked project's own version. A change inside a
    ///     method body leaves the fingerprint as it is, and a declaration change anywhere in the solution moves
    ///     it. Counting a project this file cannot see only ever binds it again, never reuses it wrongly. Null
    ///     when a project's declarations cannot be read, so its files are bound rather than reused.
    /// </summary>
    internal async Task<FileKey?> KeyAsync(Document document, CheckContext context, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        // The hash of every project's declarations is the check's, computed once for its solution: every
        // candidate of the check reads it instead of hashing the declarations of every project again.
        var declarations = await context.DeclarationsHashAsync(
            document.Project.Solution,
            _configurationGeneration(),
            ComputeAsync,
            cancellationToken).ConfigureAwait(false);
        return declarations is { } value
            ? new FileKey(Convert.ToHexString(text.GetChecksum().AsSpan()), value, _configurationGeneration())
            : null;

        async Task<int?> ComputeAsync()
        {
            var hash = new HashCode();
            foreach (var project in document.Project.Solution.Projects.OrderBy(p => p.Id.Id.ToString("N"), StringComparer.Ordinal))
            {
                hash.Add(project.Id);
                if (await DeclarationFingerprintAsync(project, cancellationToken).ConfigureAwait(false) is not { } fingerprint)
                    return null;
                hash.Add(fingerprint, StringComparer.Ordinal);
            }

            return hash.ToHashCode();
        }
    }

    /// <summary>
    ///     The declaration fingerprint of <paramref name="project"/> in its current state, computed once for that
    ///     state and read back for every later file keyed while the project stays in it. Null when its
    ///     declarations cannot be read, which the caller treats as no reuse for its files.
    /// </summary>
    private async Task<string?> DeclarationFingerprintAsync(Project project, CancellationToken cancellationToken)
    {
        try
        {
            // The project's state, read from Roslyn's versions alone: no document text is read and no syntax is
            // walked for a project whose state an earlier file of this or an earlier check already fingerprinted.
            var key = new FingerprintKey(
                project.Id,
                await project.GetSemanticVersionAsync(cancellationToken).ConfigureAwait(false),
                await project.GetLatestDocumentVersionAsync(cancellationToken).ConfigureAwait(false),
                await AdditionalStampAsync(project, cancellationToken).ConfigureAwait(false));
            if (_declarationFingerprints.TryGetValue(key, out var kept))
                return kept;
            var fingerprint = await DeclarationFingerprintCoreAsync(project, cancellationToken).ConfigureAwait(false);
            if (fingerprint is null)
                return null;
            Interlocked.Increment(ref _fingerprintComputations);
            if (_declarationFingerprints.Count > MaxDeclarationFingerprints)
                _declarationFingerprints.Clear();
            _declarationFingerprints[key] = fingerprint;
            return fingerprint;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    ///     What a project's additional documents add to its state: their ids and text versions, which a source
    ///     generator reads, and the document counts, which an added or removed file moves even when its text
    ///     declares nothing.
    /// </summary>
    private static async Task<int> AdditionalStampAsync(Project project, CancellationToken cancellationToken)
    {
        var stamp = new HashCode();
        stamp.Add(project.Documents.Count());
        stamp.Add(project.AdditionalDocuments.Count());
        foreach (var document in project.AdditionalDocuments)
        {
            stamp.Add(document.Id);
            stamp.Add(await document.GetTextVersionAsync(cancellationToken).ConfigureAwait(false));
        }

        return stamp.ToHashCode();
    }

    /// <summary>
    ///     The declarations <paramref name="project"/> holds now: every declaration's identity, surface and
    ///     parameter lists, over its documents and its source-generated documents in path order, hashed. A
    ///     default value or a constant is part of it, because callers inline them. Null when a document has no
    ///     path or its declarations cannot be read.
    /// </summary>
    private static async Task<string?> DeclarationFingerprintCoreAsync(Project project, CancellationToken cancellationToken)
    {
        var documents = project.Documents.ToList();
        documents.AddRange(await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false));
        if (documents.Any(d => d.FilePath is null))
            return null;
        documents = [.. documents.OrderBy(d => d.FilePath, StringComparer.Ordinal)];
        var builder = new StringBuilder();
        foreach (var document in documents)
        {
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

    /// <summary>HEAD's errors in one file from a run whose analyzers finished, and whether that run included the compilation-wide analyzers.</summary>
    private sealed record BaselineFile(IReadOnlyList<CompilerError> Errors, bool CompilationWide);

    /// <summary>What a working-tree file's errors were found under: its text's checksum, a hash of every project's declaration fingerprints and the configuration generation.</summary>
    internal sealed record FileKey(string Checksum, int Declarations, int Configuration);

    /// <summary>One project's state, which its declaration fingerprint is computed once for: the project, its semantic version, the version of its most recently changed document and a stamp of its additional documents and document counts.</summary>
    private readonly record struct FingerprintKey(ProjectId Project, VersionStamp Semantic, VersionStamp Latest, int Additional);

    /// <summary>A working-tree file's compiler and per-file analyzer errors, and the key they were found under.</summary>
    private sealed record CachedFile(FileKey Key, IReadOnlyList<CompilerError> Errors);
}
