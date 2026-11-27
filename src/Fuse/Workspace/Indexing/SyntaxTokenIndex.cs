using System.Collections.Immutable;
using Fuse.Paths;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
namespace Fuse.Workspace.Indexing;

/// <summary>
///     Persistent syntax-only inverted index mapping identifier tokens and declared base types to documents.
///     Queries avoid <see cref="SemanticModel"/> loading and narrow symbol searches to candidate documents.
/// </summary>
/// <remarks>
///     Each indexed entry is keyed by path and preprocessor configuration so multi-targeted
///     projects keep distinct conditional compilation branches without overwriting each other.
/// </remarks>
internal sealed class SyntaxTokenIndex
{
    private static readonly Lock _liveLock = new();
    private static readonly List<WeakReference<SyntaxTokenIndex>> _live = [];

    private readonly RepoRoot _root;
    private readonly Action<string> _log;
    private readonly TokenIndexStore _store;
    private readonly Lock _lock = new();
    private readonly Lock _reloadLock = new();

    private readonly List<TokenIndexStore.DocEntry?> _entries = [];
    private readonly Dictionary<(RepoPath Path, string PreprocessorKey), int> _entryLookup = [];
    private readonly Dictionary<DocumentId, (VersionStamp Version, int EntryIndex)> _docVersions = [];
    private readonly Dictionary<string, HashSet<int>> _tokenToEntries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<int>> _baseTypeToEntries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<int>> _typeNameToEntries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _globalAliases = new(StringComparer.Ordinal);
    private bool _dirty;
    private bool _writeLogged;
    private string? _lastKey;
    private int _released;

    /// <param name="root">The repository, for canonical relative paths and shared state persistence.</param>
    /// <param name="log">Optional sink for persistence diagnostics.</param>
    public SyntaxTokenIndex(RepoRoot root, Action<string>? log = null)
    {
        _root = root;
        _log = log ?? (_ => { });
        _store = new TokenIndexStore(root);
        RegisterLive(this);
    }

    private static void RegisterLive(SyntaxTokenIndex index)
    {
        lock (_liveLock)
        {
            for (var i = _live.Count - 1; i >= 0; i--)
            {
                if (!_live[i].TryGetTarget(out _))
                    _live.RemoveAt(i);
            }

            _live.Add(new WeakReference<SyntaxTokenIndex>(index));
        }
    }

    /// <summary>
    ///     Releases every live index's in-memory entries, after persisting them, so an idle engine returns that
    ///     memory; the next query loads them from disk again instead of parsing every file.
    /// </summary>
    /// <returns>How many document entries were dropped across every live index.</returns>
    public static int ReleaseIdleAll()
    {
        List<SyntaxTokenIndex> targets;
        lock (_liveLock)
        {
            targets = [];
            for (var i = _live.Count - 1; i >= 0; i--)
            {
                if (_live[i].TryGetTarget(out var target))
                    targets.Add(target);
                else
                    _live.RemoveAt(i);
            }
        }

        var dropped = 0;
        foreach (var target in targets)
            dropped += target.ReleaseIdle();
        return dropped;
    }

    /// <summary>
    ///     Persists this index and drops its in-memory entries, so an idle engine returns that memory; the next
    ///     query loads them from disk again instead of parsing every file.
    /// </summary>
    /// <returns>How many document entries were dropped.</returns>
    public int ReleaseIdle()
    {
        string? key;
        lock (_lock)
            key = _lastKey;
        SaveIfDirty(key);
        lock (_lock)
        {
            if (_lastKey is null && _entryLookup.Count == 0 && !_dirty)
                return 0;
            var dropped = _entryLookup.Count;
            ClearLocked();
            Interlocked.Exchange(ref _released, 1);
            return dropped;
        }
    }

    /// <summary>
    ///     Loads the persisted entries back after <see cref="ReleaseIdle"/> dropped them, once: the first query
    ///     loads from disk while every concurrent one waits behind it, so none of them parses every file again.
    /// </summary>
    private void ReloadIfReleased()
    {
        if (Volatile.Read(ref _released) == 0)
            return;
        lock (_reloadLock)
        {
            if (Interlocked.Exchange(ref _released, 0) == 0)
                return;
            string? key;
            lock (_lock)
                key = _lastKey;
            TryLoad(key);
        }
    }

    /// <summary>Directory under <see cref="RepoRoot.SharedStateDirectory"/> where persisted index files live.</summary>
    public string Directory => _store.Directory;

    /// <summary>How many document entries are currently held in memory.</summary>
    public int DocumentCount
    {
        get
        {
            lock (_lock)
                return _entryLookup.Count;
        }
    }

    /// <summary>
    ///     Ensures every C# document in <paramref name="solution"/> is indexed and current.
    ///     Only documents whose version, text checksum, or disk stamp changed are parsed.
    /// </summary>
    public Task EnsureIndexedAsync(Solution solution, CancellationToken cancellationToken = default) =>
        EnsureIndexedAsync(solution, solution.Projects, cancellationToken);

    /// <summary>
    ///     Ensures every C# document in <paramref name="projects"/> is indexed and current.
    ///     Only documents whose version, text checksum, or disk stamp changed are parsed.
    /// </summary>
    public async Task EnsureIndexedAsync(Solution solution, IEnumerable<Project> projects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(projects);
        ReloadIfReleased();

        var toParse = new List<(Document Document, RepoPath Path, string PreprocessorKey, long Length, long WrittenTicks, string Checksum)>();

        foreach (var project in projects)
        {
            if (project.Language != LanguageNames.CSharp)
                continue;
            var preprocessorKey = PreprocessorKeyFor(project);
            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(document.FilePath))
                    continue;

                var version = await document.GetTextVersionAsync(cancellationToken).ConfigureAwait(false);
                var path = NormalizePath(document.FilePath);
                if (IsEntryCurrent(document, path, preprocessorKey, version, out var length, out var writtenTicks, out var checksum))
                {
                    LinkSkipped(document, path, preprocessorKey, version);
                    continue;
                }

                toParse.Add((document, path, preprocessorKey, length, writtenTicks, checksum));
            }
        }

        if (toParse.Count == 0)
            return;

        var scanned = new ScannedEntry?[toParse.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, toParse.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (index, ct) =>
            {
                var item = toParse[index];
                var root = await item.Document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                if (root is null)
                    return;
                var checksum = item.Checksum;
                if (checksum.Length == 0)
                {
                    var text = await item.Document.GetTextAsync(ct).ConfigureAwait(false);
                    checksum = ChecksumOf(text);
                }

                var version = await item.Document.GetTextVersionAsync(ct).ConfigureAwait(false);
                scanned[index] = new ScannedEntry(
                    item.Document.Id,
                    version,
                    item.Path,
                    item.PreprocessorKey,
                    item.Length,
                    item.WrittenTicks,
                    checksum,
                    SyntaxDocumentScanner.Scan(root));
            }).ConfigureAwait(false);

        lock (_lock)
        {
            foreach (var item in scanned)
            {
                if (item is null)
                    continue;
                var entryIndex = UpsertLocked(
                    item.Path,
                    item.PreprocessorKey,
                    item.Length,
                    item.WrittenTicks,
                    item.Checksum,
                    item.Scan);
                _docVersions[item.DocumentId] = (item.Version, entryIndex);
            }
        }
    }

    /// <summary>
    ///     Incrementally updates the index for one document from its syntax root without touching any semantic model.
    /// </summary>
    public void UpdateDocument(string filePath, SyntaxNode root, string preprocessorKey = "", string checksum = "", long length = 0, long writtenTicks = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(root);
        ReloadIfReleased();

        var path = NormalizePath(filePath);
        if (length == 0 && writtenTicks == 0)
            TryGetDiskStamp(path.Absolute, out length, out writtenTicks);
        if (checksum.Length == 0 && root.SyntaxTree.TryGetText(out var text))
            checksum = ChecksumOf(text);

        var scan = SyntaxDocumentScanner.Scan(root);
        lock (_lock)
            UpsertLocked(path, preprocessorKey, length, writtenTicks, checksum, scan);
    }

    /// <summary>Incrementally updates the index for <paramref name="document"/>.</summary>
    public async Task UpdateDocumentAsync(Document document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ReloadIfReleased();
        if (string.IsNullOrEmpty(document.FilePath))
            return;
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
            return;
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var version = await document.GetTextVersionAsync(cancellationToken).ConfigureAwait(false);
        var path = NormalizePath(document.FilePath);
        var preprocessorKey = PreprocessorKeyFor(document.Project);
        TryGetDiskStamp(path.Absolute, out var length, out var writtenTicks);
        var scan = SyntaxDocumentScanner.Scan(root);

        lock (_lock)
        {
            var entryIndex = UpsertLocked(path, preprocessorKey, length, writtenTicks, ChecksumOf(text), scan);
            _docVersions[document.Id] = (version, entryIndex);
        }
    }

    /// <summary>Removes all indexed entries for <paramref name="filePath"/>.</summary>
    public void RemoveDocument(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ReloadIfReleased();
        var path = NormalizePath(filePath);
        lock (_lock)
        {
            var keys = _entryLookup.Keys.Where(k => k.Path == path).ToList();
            foreach (var key in keys)
                RemoveLocked(key);
        }
    }

    /// <summary>
    ///     Queries candidate documents in <paramref name="solution"/> mentioning any of <paramref name="identifiers"/>
    ///     or transitively deriving from or implementing any of <paramref name="baseTypeNames"/>.
    /// </summary>
    /// <param name="solution">The solution whose documents are filtered.</param>
    /// <param name="identifiers">Identifier token names to match (attribute names also match omitted <c>Attribute</c> suffix).</param>
    /// <param name="baseTypeNames">Base type or interface simple names to expand transitively across syntax inheritance.</param>
    /// <param name="scopeProjects">Optional project filter restricting returned documents.</param>
    public ImmutableHashSet<Document> CandidateDocuments(
        Solution solution,
        IEnumerable<string> identifiers,
        IEnumerable<string> baseTypeNames,
        IReadOnlySet<ProjectId>? scopeProjects = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(identifiers);
        ArgumentNullException.ThrowIfNull(baseTypeNames);

        var identifierList = identifiers as IReadOnlyCollection<string> ?? [.. identifiers];
        var baseList = baseTypeNames as IReadOnlyCollection<string> ?? [.. baseTypeNames];
        ReloadIfReleased();

        var projects = scopeProjects is null
            ? solution.Projects
            : solution.Projects.Where(p => scopeProjects.Contains(p.Id));
        EnsureIndexedSyncIfNeeded(solution, projects);

        var matchingPaths = CandidateRepoPaths(identifierList, baseList);
        var builder = ImmutableHashSet.CreateBuilder<Document>();
        foreach (var project in projects)
        {
            if (project.Language != LanguageNames.CSharp)
                continue;
            foreach (var document in project.Documents)
            {
                if (string.IsNullOrEmpty(document.FilePath) || matchingPaths.Contains(NormalizePath(document.FilePath)))
                    builder.Add(document);
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    ///     Queries candidate documents from the entries already held, without parsing or hashing, for a search on
    ///     the request path; after an idle release it loads the persisted entries from disk once first. Documents
    ///     whose indexed entry is current narrow by token; every other document is a candidate too, so the answer
    ///     is the same set a search without narrowing covers while the background warm (see
    ///     <c>EnsureIndexedAsync</c>) has not indexed it yet.
    /// </summary>
    /// <param name="solution">The solution whose documents are filtered.</param>
    /// <param name="identifiers">Identifier token names to match (attribute names also match omitted <c>Attribute</c> suffix).</param>
    /// <param name="baseTypeNames">Base type or interface simple names to expand transitively across syntax inheritance.</param>
    /// <param name="scopeProjects">Optional project filter restricting returned documents.</param>
    public ImmutableHashSet<Document> CandidateDocumentsSnapshot(
        Solution solution,
        IEnumerable<string> identifiers,
        IEnumerable<string> baseTypeNames,
        IReadOnlySet<ProjectId>? scopeProjects = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(identifiers);
        ArgumentNullException.ThrowIfNull(baseTypeNames);

        var identifierList = identifiers as IReadOnlyCollection<string> ?? [.. identifiers];
        var baseList = baseTypeNames as IReadOnlyCollection<string> ?? [.. baseTypeNames];

        var projects = scopeProjects is null
            ? solution.Projects
            : solution.Projects.Where(p => scopeProjects.Contains(p.Id));
        ReloadIfReleased();
        var matchingPaths = CandidateRepoPaths(identifierList, baseList);
        var builder = ImmutableHashSet.CreateBuilder<Document>();
        foreach (var project in projects)
        {
            if (project.Language != LanguageNames.CSharp)
                continue;
            var preprocessorKey = PreprocessorKeyFor(project);
            foreach (var document in project.Documents)
            {
                if (string.IsNullOrEmpty(document.FilePath))
                {
                    builder.Add(document);
                    continue;
                }

                var path = NormalizePath(document.FilePath);
                if (matchingPaths.Contains(path) || !IsIndexedAndCurrent(document, path, preprocessorKey))
                    builder.Add(document);
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    ///     True when the entry held for <paramref name="document"/> was scanned from the text it holds now: its path
    ///     and preprocessor entry exists, and the document's id links to it with its current text version. Same
    ///     version is same text, so only such a document narrows by token; anything else is searched fully.
    /// </summary>
    private bool IsIndexedAndCurrent(Document document, RepoPath path, string preprocessorKey)
    {
        if (!document.TryGetTextVersion(out var version))
            return false;
        lock (_lock)
        {
            return _entryLookup.TryGetValue((path, preprocessorKey), out var entryIndex)
                && _entries[entryIndex] is not null
                && _docVersions.TryGetValue(document.Id, out var known)
                && known.EntryIndex == entryIndex
                && known.Version == version;
        }
    }

    /// <summary>
    ///     Async variant of <see cref="CandidateDocuments"/> that indexes missing or stale documents concurrently first.
    /// </summary>
    public async Task<ImmutableHashSet<Document>> CandidateDocumentsAsync(
        Solution solution,
        IEnumerable<string> identifiers,
        IEnumerable<string> baseTypeNames,
        IReadOnlySet<ProjectId>? scopeProjects = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(identifiers);
        ArgumentNullException.ThrowIfNull(baseTypeNames);

        var projects = (scopeProjects is null
            ? solution.Projects
            : solution.Projects.Where(p => scopeProjects.Contains(p.Id))).ToList();
        ReloadIfReleased();
        await EnsureIndexedAsync(solution, projects, cancellationToken).ConfigureAwait(false);
        return CandidateDocuments(solution, identifiers, baseTypeNames, scopeProjects);
    }

    /// <summary>
    ///     Returns candidate repository-relative paths mentioning any of <paramref name="identifiers"/> or transitively
    ///     deriving from or implementing any of <paramref name="baseTypeNames"/>.
    /// </summary>
    public HashSet<string> CandidatePaths(IEnumerable<string> identifiers, IEnumerable<string> baseTypeNames)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        ArgumentNullException.ThrowIfNull(baseTypeNames);
        ReloadIfReleased();

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in CandidateRepoPaths(identifiers, baseTypeNames))
            result.Add(path.Relative);
        return result;
    }

    private HashSet<RepoPath> CandidateRepoPaths(IEnumerable<string> identifiers, IEnumerable<string> baseTypeNames)
    {
        var result = new HashSet<RepoPath>();
        lock (_lock)
        {
            var matchedEntries = new HashSet<int>();
            var queryTokens = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in identifiers)
            {
                if (string.IsNullOrEmpty(id))
                    continue;
                queryTokens.Add(id);
                if (id.Length > "Attribute".Length && id.EndsWith("Attribute", StringComparison.Ordinal))
                    queryTokens.Add(id[..^"Attribute".Length]);
                if (_globalAliases.TryGetValue(id, out var aliases))
                    queryTokens.UnionWith(aliases);
            }

            foreach (var token in queryTokens)
            {
                if (_tokenToEntries.TryGetValue(token, out var entries))
                    matchedEntries.UnionWith(entries);
            }

            var queue = new Queue<string>();
            var visitedTypes = new HashSet<string>(StringComparer.Ordinal);
            void EnqueueType(string name)
            {
                if (string.IsNullOrEmpty(name))
                    return;
                if (visitedTypes.Add(name))
                    queue.Enqueue(name);
                if (_globalAliases.TryGetValue(name, out var aliases))
                {
                    foreach (var alias in aliases)
                    {
                        if (visitedTypes.Add(alias))
                            queue.Enqueue(alias);
                    }
                }
            }

            foreach (var baseName in baseTypeNames)
                EnqueueType(baseName);

            while (queue.Count > 0)
            {
                var typeName = queue.Dequeue();
                if (!_baseTypeToEntries.TryGetValue(typeName, out var derivedEntries))
                    continue;
                foreach (var entryIndex in derivedEntries)
                {
                    matchedEntries.Add(entryIndex);
                    if (_entries[entryIndex] is not { } entry)
                        continue;
                    foreach (var declared in entry.DeclaredTypes)
                    {
                        EnqueueType(declared);
                        if (_typeNameToEntries.TryGetValue(declared, out var partialEntries))
                            matchedEntries.UnionWith(partialEntries);
                    }
                }
            }

            foreach (var entryIndex in matchedEntries)
            {
                if (_entries[entryIndex] is { } entry)
                    result.Add(entry.Path);
            }
        }

        return result;
    }

    /// <summary>Persists the current index to <see cref="Directory"/> under <paramref name="key"/> only if modified since last save or load.</summary>
    public void SaveIfDirty(string? key = null)
    {
        bool shouldSave;
        lock (_lock)
            shouldSave = _dirty;
        if (shouldSave)
            Save(key);
    }

    /// <summary>Persists the current index to <see cref="Directory"/> under <paramref name="key"/>.</summary>
    public void Save(string? key = null)
    {
        var file = _store.FilePathFor(key);
        var staging = file + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        List<TokenIndexStore.DocEntry> snapshot;
        lock (_lock)
        {
            snapshot = [.. _entries.OfType<TokenIndexStore.DocEntry>()];
            _dirty = false;
            _lastKey = key;
        }

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            TokenIndexStore.WriteBinary(staging, snapshot);
            using var writeLock = TokenIndexStore.AcquireWriteLock(Directory);
            if (writeLock is null)
            {
                TokenIndexStore.TryDelete(staging);
                return;
            }

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(staging, file, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TokenIndexStore.TryDelete(staging);
            if (!_writeLogged)
            {
                _writeLogged = true;
                _log($"token index write failed: {e.Message}");
            }
        }
    }

    /// <summary>Loads a persisted index from <see cref="Directory"/> under <paramref name="key"/>, validating disk stamps. A file that does not read is deleted so the next save writes it again.</summary>
    public bool TryLoad(string? key = null)
    {
        Interlocked.Exchange(ref _released, 0);
        lock (_lock)
            _lastKey = key;
        var file = _store.FilePathFor(key);
        if (!File.Exists(file))
            return false;

        try
        {
            var loaded = _store.ReadBinary(file);
            lock (_lock)
            {
                ClearLocked();
                foreach (var entry in loaded)
                {
                    if (!TryGetDiskStamp(entry.Path.Absolute, out var length, out var writtenTicks))
                        continue;
                    if (length != entry.Length || writtenTicks != entry.WrittenTicks)
                        continue;

                    UpsertLocked(
                        entry.Path,
                        entry.PreprocessorKey,
                        entry.Length,
                        entry.WrittenTicks,
                        entry.Checksum,
                        new SyntaxDocumentScanner.ScanResult(entry.Tokens, entry.BaseTypes, entry.DeclaredTypes, entry.GlobalAliases));
                }

                _dirty = false;
            }

            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            lock (_lock)
                ClearLocked();
            TokenIndexStore.TryDelete(file);
            return false;
        }
    }

    /// <summary>Clears all in-memory index entries.</summary>
    public void Clear()
    {
        lock (_lock)
            ClearLocked();
    }

    private void ClearLocked()
    {
        _entries.Clear();
        _entryLookup.Clear();
        _docVersions.Clear();
        _tokenToEntries.Clear();
        _baseTypeToEntries.Clear();
        _typeNameToEntries.Clear();
        _globalAliases.Clear();
    }

    private void EnsureIndexedSyncIfNeeded(Solution solution, IEnumerable<Project> projects)
    {
        var toParse = new List<(Document Document, RepoPath Path, string PreprocessorKey, long Length, long WrittenTicks, string Checksum)>();
        foreach (var project in projects)
        {
            if (project.Language != LanguageNames.CSharp)
                continue;
            var preprocessorKey = PreprocessorKeyFor(project);
            foreach (var document in project.Documents)
            {
                if (string.IsNullOrEmpty(document.FilePath))
                    continue;
                var version = document.TryGetTextVersion(out var v) ? v : default;
                var path = NormalizePath(document.FilePath);
                if (IsEntryCurrent(document, path, preprocessorKey, version, out var length, out var writtenTicks, out var checksum))
                {
                    LinkSkipped(document, path, preprocessorKey, version);
                    continue;
                }

                toParse.Add((document, path, preprocessorKey, length, writtenTicks, checksum));
            }
        }

        if (toParse.Count == 0)
            return;

        foreach (var item in toParse)
        {
            var root = item.Document.TryGetSyntaxRoot(out var readyRoot)
                ? readyRoot
                : item.Document.GetSyntaxRootAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (root is null)
                continue;
            var checksum = item.Checksum;
            if (checksum.Length == 0)
            {
                var text = item.Document.TryGetText(out var readyText)
                    ? readyText
                    : item.Document.GetTextAsync(CancellationToken.None).GetAwaiter().GetResult();
                checksum = ChecksumOf(text);
            }

            var version = item.Document.GetTextVersionAsync(CancellationToken.None).GetAwaiter().GetResult();
            var scan = SyntaxDocumentScanner.Scan(root);
            lock (_lock)
            {
                var entryIndex = UpsertLocked(item.Path, item.PreprocessorKey, item.Length, item.WrittenTicks, checksum, scan);
                _docVersions[item.Document.Id] = (version, entryIndex);
            }
        }
    }

    private bool IsEntryCurrent(
        Document document,
        RepoPath path,
        string preprocessorKey,
        VersionStamp version,
        out long length,
        out long writtenTicks,
        out string checksum)
    {
        length = 0;
        writtenTicks = 0;
        checksum = "";

        if (document.TryGetText(out var loadedText))
            checksum = ChecksumOf(loadedText);

        if (document.FilePath is not null)
            TryGetDiskStamp(document.FilePath, out length, out writtenTicks);

        lock (_lock)
        {
            if (version != default
                && _docVersions.TryGetValue(document.Id, out var known)
                && known.Version == version
                && _entries[known.EntryIndex] is { } knownEntry
                && (checksum.Length == 0 || string.Equals(knownEntry.Checksum, checksum, StringComparison.Ordinal)))
            {
                return true;
            }

            if (!_entryLookup.TryGetValue((path, preprocessorKey), out var entryIndex) || _entries[entryIndex] is not { } entry)
                return false;

            if (checksum.Length > 0)
                return string.Equals(entry.Checksum, checksum, StringComparison.Ordinal);

            return length > 0 && entry.Length == length && entry.WrittenTicks == writtenTicks;
        }
    }

    /// <summary>
    ///     Links <paramref name="document"/> at <paramref name="version"/> to its current entry, after
    ///     <see cref="IsEntryCurrent"/> skipped scanning it, so a later snapshot query narrows it by token. Only
    ///     links when the entry held for its path and preprocessor entry can be the one the document holds now: a
    ///     document linked to another entry was scanned under another preprocessor entry, and is searched fully
    ///     until it is scanned under this one.
    /// </summary>
    private void LinkSkipped(Document document, RepoPath path, string preprocessorKey, VersionStamp version)
    {
        if (version == default)
            return;
        lock (_lock)
        {
            if (!_entryLookup.TryGetValue((path, preprocessorKey), out var entryIndex) || _entries[entryIndex] is null)
                return;
            if (_docVersions.TryGetValue(document.Id, out var known) && known.EntryIndex != entryIndex)
                return;
            _docVersions[document.Id] = (version, entryIndex);
        }
    }

    private int UpsertLocked(
        RepoPath path,
        string preprocessorKey,
        long length,
        long writtenTicks,
        string checksum,
        SyntaxDocumentScanner.ScanResult scan)
    {
        var key = (path, preprocessorKey);
        if (_entryLookup.TryGetValue(key, out var existingIndex) && _entries[existingIndex] is { } existing)
            UnlinkEntryLocked(existingIndex, existing);
        else
        {
            existingIndex = _entries.Count;
            _entries.Add(null);
            _entryLookup[key] = existingIndex;
        }

        var entry = new TokenIndexStore.DocEntry(
            path,
            preprocessorKey,
            length,
            writtenTicks,
            checksum,
            scan.Tokens,
            scan.BaseTypes,
            scan.DeclaredTypes,
            scan.GlobalAliases);
        _entries[existingIndex] = entry;
        LinkEntryLocked(existingIndex, entry);
        _dirty = true;
        return existingIndex;
    }

    private void RemoveLocked((RepoPath Path, string PreprocessorKey) key)
    {
        if (!_entryLookup.Remove(key, out var index) || _entries[index] is not { } entry)
            return;
        UnlinkEntryLocked(index, entry);
        _entries[index] = null;
        _dirty = true;
        foreach (var docId in _docVersions.Where(kv => kv.Value.EntryIndex == index).Select(kv => kv.Key).ToList())
            _docVersions.Remove(docId);
    }

    private void LinkEntryLocked(int entryIndex, TokenIndexStore.DocEntry entry)
    {
        foreach (var token in entry.Tokens)
            AddIndex(_tokenToEntries, token, entryIndex);
        foreach (var baseType in entry.BaseTypes)
            AddIndex(_baseTypeToEntries, baseType, entryIndex);
        foreach (var declared in entry.DeclaredTypes)
            AddIndex(_typeNameToEntries, declared, entryIndex);
        foreach (var (alias, target) in entry.GlobalAliases)
        {
            if (!_globalAliases.TryGetValue(target, out var set))
                _globalAliases[target] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(alias);
        }
    }

    private void UnlinkEntryLocked(int entryIndex, TokenIndexStore.DocEntry entry)
    {
        foreach (var token in entry.Tokens)
            RemoveIndex(_tokenToEntries, token, entryIndex);
        foreach (var baseType in entry.BaseTypes)
            RemoveIndex(_baseTypeToEntries, baseType, entryIndex);
        foreach (var declared in entry.DeclaredTypes)
            RemoveIndex(_typeNameToEntries, declared, entryIndex);
        foreach (var (alias, target) in entry.GlobalAliases)
        {
            if (_globalAliases.TryGetValue(target, out var set))
            {
                set.Remove(alias);
                if (set.Count == 0)
                    _globalAliases.Remove(target);
            }
        }
    }

    private static void AddIndex(Dictionary<string, HashSet<int>> map, string key, int entryIndex)
    {
        if (!map.TryGetValue(key, out var set))
            map[key] = set = [];
        set.Add(entryIndex);
    }

    private static void RemoveIndex(Dictionary<string, HashSet<int>> map, string key, int entryIndex)
    {
        if (!map.TryGetValue(key, out var set))
            return;
        set.Remove(entryIndex);
        if (set.Count == 0)
            map.Remove(key);
    }

    private RepoPath NormalizePath(string filePath)
    {
        try
        {
            return _root.PathOf(filePath);
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return new RepoPath(_root, filePath.Replace('\0', '_'));
        }
    }

    private static string PreprocessorKeyFor(Project project)
    {
        if (project.ParseOptions is not CSharpParseOptions csharp)
            return "";
        var symbols = string.Join(";", csharp.PreprocessorSymbolNames.Order(StringComparer.Ordinal));
        return $"{(int)csharp.LanguageVersion}:{symbols}";
    }

    private static string ChecksumOf(SourceText text) =>
        Convert.ToHexString(text.GetChecksum().AsSpan());

    private static bool TryGetDiskStamp(string absolutePath, out long length, out long writtenTicks)
    {
        length = 0;
        writtenTicks = 0;
        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists)
                return false;
            length = info.Length;
            writtenTicks = info.LastWriteTimeUtc.Ticks;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private sealed record ScannedEntry(
        DocumentId DocumentId,
        VersionStamp Version,
        RepoPath Path,
        string PreprocessorKey,
        long Length,
        long WrittenTicks,
        string Checksum,
        SyntaxDocumentScanner.ScanResult Scan);
}
