using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Fuse.Paths;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Fuse.Workspace;

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
    private const uint BinaryMagic = 0x58444946; // "FIDX"
    private const int BinaryVersion = 2;
    private const int WriteLockAttempts = 20;
    private const int WriteLockRetryMs = 50;

    private readonly RepoRoot _root;
    private readonly Action<string> _log;
    private readonly Lock _lock = new();

    private readonly List<DocEntry?> _entries = [];
    private readonly Dictionary<(RepoPath Path, string PreprocessorKey), int> _entryLookup = [];
    private readonly Dictionary<DocumentId, (VersionStamp Version, int EntryIndex)> _docVersions = [];
    private readonly Dictionary<string, HashSet<int>> _tokenToEntries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<int>> _baseTypeToEntries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<int>> _typeNameToEntries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _globalAliases = new(StringComparer.Ordinal);
    private bool _dirty;
    private bool _writeLogged;

    /// <param name="root">The repository, for canonical relative paths and shared state persistence.</param>
    /// <param name="log">Optional sink for persistence diagnostics.</param>
    public SyntaxTokenIndex(RepoRoot root, Action<string>? log = null)
    {
        _root = root;
        _log = log ?? (_ => { });
    }

    /// <summary>Directory under <see cref="RepoRoot.SharedStateDirectory"/> where persisted index files live.</summary>
    public string Directory => Path.Combine(_root.SharedStateDirectory, "token-index");

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
                    Scan(root));
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

        var path = NormalizePath(filePath);
        if (length == 0 && writtenTicks == 0)
            TryGetDiskStamp(path.Absolute, out length, out writtenTicks);
        if (checksum.Length == 0 && root.SyntaxTree.TryGetText(out var text))
            checksum = ChecksumOf(text);

        var scan = Scan(root);
        lock (_lock)
            UpsertLocked(path, preprocessorKey, length, writtenTicks, checksum, scan);
    }

    /// <summary>Incrementally updates the index for <paramref name="document"/>.</summary>
    public async Task UpdateDocumentAsync(Document document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
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
        var scan = Scan(root);

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
    ///     Queries candidate documents from the entries already held, without parsing, hashing or touching disk, for a
    ///     search on the request path. Documents whose indexed entry is current narrow by token; every other document
    ///     is a candidate too, so the answer is the same set a search without narrowing covers while the background
    ///     warm (see <c>EnsureIndexedAsync</c>) has not indexed it yet.
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
        var file = FilePathFor(key);
        var staging = file + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        List<DocEntry> snapshot;
        lock (_lock)
        {
            snapshot = [.. _entries.Where(e => e is not null).Select(e => e!)];
            _dirty = false;
        }

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            WriteBinary(staging, snapshot);
            using var writeLock = AcquireWriteLock(Directory);
            if (writeLock is null)
            {
                TryDelete(staging);
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
            TryDelete(staging);
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
        var file = FilePathFor(key);
        if (!File.Exists(file))
            return false;

        try
        {
            var loaded = ReadBinary(file);
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
                        new ScanResult(entry.Tokens, entry.BaseTypes, entry.DeclaredTypes, entry.GlobalAliases));
                }

                _dirty = false;
            }

            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            lock (_lock)
                ClearLocked();
            TryDelete(file);
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
            var scan = Scan(root);
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
        ScanResult scan)
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

        var entry = new DocEntry(
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

    private void LinkEntryLocked(int entryIndex, DocEntry entry)
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

    private void UnlinkEntryLocked(int entryIndex, DocEntry entry)
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

    private static ScanResult Scan(SyntaxNode root)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var baseTypes = new HashSet<string>(StringComparer.Ordinal);
        var declaredTypes = new HashSet<string>(StringComparer.Ordinal);
        var localAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var globalAliases = new List<(string Alias, string Target)>();

        foreach (var token in root.DescendantTokens(descendIntoTrivia: true))
        {
            if (!token.IsKind(SyntaxKind.IdentifierToken))
                continue;
            var valueText = token.ValueText;
            if (string.IsNullOrEmpty(valueText))
                continue;
            tokens.Add(valueText);

            if (IsAttributeNameToken(token) && !valueText.EndsWith("Attribute", StringComparison.Ordinal))
                tokens.Add(valueText + "Attribute");
        }

        WalkTypeHierarchy(root, tokens, baseTypes, declaredTypes, localAliases, globalAliases);
        return new ScanResult([.. tokens], [.. baseTypes], [.. declaredTypes], [.. globalAliases]);
    }

    private static bool IsAttributeNameToken(SyntaxToken token)
    {
        if (token.Parent is not SimpleNameSyntax name)
            return false;
        return name.Parent switch
        {
            AttributeSyntax attribute => attribute.Name == name,
            QualifiedNameSyntax qualified => qualified.Right == name && qualified.Parent is AttributeSyntax attribute && attribute.Name == qualified,
            AliasQualifiedNameSyntax alias => alias.Name == name && alias.Parent is AttributeSyntax attribute && attribute.Name == alias,
            _ => false,
        };
    }

    private static void WalkTypeHierarchy(
        SyntaxNode node,
        HashSet<string> tokens,
        HashSet<string> baseTypes,
        HashSet<string> declaredTypes,
        Dictionary<string, string> localAliases,
        List<(string Alias, string Target)> globalAliases)
    {
        foreach (var child in node.ChildNodes())
        {
            switch (child)
            {
                case UsingDirectiveSyntax directive when directive.Alias is { } alias && directive.NamespaceOrType is { } targetNode:
                    if (RightmostIdentifier(targetNode) is { } targetName)
                    {
                        var aliasName = alias.Name.Identifier.ValueText;
                        if (directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))
                            globalAliases.Add((aliasName, targetName));
                        else
                            localAliases[aliasName] = targetName;
                    }

                    break;

                case BaseNamespaceDeclarationSyntax ns:
                    WalkTypeHierarchy(ns, tokens, baseTypes, declaredTypes, localAliases, globalAliases);
                    break;

                case DelegateDeclarationSyntax del:
                    declaredTypes.Add(del.Identifier.ValueText);
                    break;

                case BaseTypeDeclarationSyntax type:
                    if (type is not ExtensionBlockDeclarationSyntax)
                        declaredTypes.Add(type.Identifier.ValueText);
                    if (type.BaseList is { } baseList)
                    {
                        foreach (var bt in baseList.Types)
                        {
                            if (RightmostIdentifier(bt.Type) is not { } baseName)
                                continue;
                            baseTypes.Add(baseName);
                            if (localAliases.TryGetValue(baseName, out var resolved))
                                baseTypes.Add(resolved);
                        }
                    }

                    if (type is TypeDeclarationSyntax typeDeclaration)
                        WalkTypeHierarchy(typeDeclaration, tokens, baseTypes, declaredTypes, localAliases, globalAliases);
                    break;
            }
        }
    }

    private static string? RightmostIdentifier(TypeSyntax type) =>
        type switch
        {
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            _ => null,
        };

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

    private string FilePathFor(string? key)
    {
        var name = string.IsNullOrWhiteSpace(key)
            ? "workspace"
            : IsSafeFileName(key)
                ? key
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        return Path.Combine(Directory, name + ".bin");
    }

    private static bool IsSafeFileName(string key) =>
        key.Length <= 64 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

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

    private static void WriteBinary(string path, List<DocEntry> entries)
    {
        var stringIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var strings = new List<string>();
        int IdOf(string value)
        {
            if (stringIds.TryGetValue(value, out var id))
                return id;
            id = strings.Count;
            strings.Add(value);
            stringIds[value] = id;
            return id;
        }

        foreach (var entry in entries)
        {
            IdOf(entry.PreprocessorKey);
            foreach (var token in entry.Tokens)
                IdOf(token);
            foreach (var baseType in entry.BaseTypes)
                IdOf(baseType);
            foreach (var declared in entry.DeclaredTypes)
                IdOf(declared);
            foreach (var (alias, target) in entry.GlobalAliases)
            {
                IdOf(alias);
                IdOf(target);
            }
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(BinaryMagic);
        writer.Write(BinaryVersion);

        writer.Write7BitEncodedInt(strings.Count);
        foreach (var text in strings)
            writer.Write(text);

        writer.Write7BitEncodedInt(entries.Count);
        foreach (var entry in entries)
        {
            writer.Write(entry.Path.Relative);
            writer.Write7BitEncodedInt(IdOf(entry.PreprocessorKey));
            writer.Write(entry.Length);
            writer.Write(entry.WrittenTicks);
            writer.Write(entry.Checksum);

            WriteIdArray(writer, entry.Tokens, IdOf);
            WriteIdArray(writer, entry.BaseTypes, IdOf);
            WriteIdArray(writer, entry.DeclaredTypes, IdOf);

            writer.Write7BitEncodedInt(entry.GlobalAliases.Length);
            foreach (var (alias, target) in entry.GlobalAliases)
            {
                writer.Write7BitEncodedInt(IdOf(alias));
                writer.Write7BitEncodedInt(IdOf(target));
            }
        }
    }

    private static void WriteIdArray(BinaryWriter writer, string[] items, Func<string, int> idOf)
    {
        writer.Write7BitEncodedInt(items.Length);
        foreach (var item in items)
            writer.Write7BitEncodedInt(idOf(item));
    }

    private List<DocEntry> ReadBinary(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (reader.ReadUInt32() != BinaryMagic || reader.ReadInt32() != BinaryVersion)
            throw new InvalidDataException("unsupported token index header");

        var stringCount = reader.Read7BitEncodedInt();
        if (stringCount < 0 || stringCount > stream.Length)
            throw new InvalidDataException("token index string table is out of range");
        var strings = new string[stringCount];
        for (var i = 0; i < stringCount; i++)
            strings[i] = reader.ReadString();

        var entryCount = reader.Read7BitEncodedInt();
        if (entryCount < 0 || entryCount > stream.Length)
            throw new InvalidDataException("token index entry table is out of range");
        var entries = new List<DocEntry>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var relative = reader.ReadString();
            var preprocessorKey = StringAt(strings, reader.Read7BitEncodedInt());
            var length = reader.ReadInt64();
            var writtenTicks = reader.ReadInt64();
            var checksum = reader.ReadString();

            var tokens = ReadIdArray(reader, strings, stream.Length);
            var baseTypes = ReadIdArray(reader, strings, stream.Length);
            var declaredTypes = ReadIdArray(reader, strings, stream.Length);

            var aliasCount = reader.Read7BitEncodedInt();
            if (aliasCount < 0 || aliasCount > stream.Length)
                throw new InvalidDataException("token index alias table is out of range");
            var aliases = aliasCount == 0 ? [] : new (string, string)[aliasCount];
            for (var a = 0; a < aliasCount; a++)
                aliases[a] = (StringAt(strings, reader.Read7BitEncodedInt()), StringAt(strings, reader.Read7BitEncodedInt()));

            entries.Add(new DocEntry(_root.PathOf(relative), preprocessorKey, length, writtenTicks, checksum, tokens, baseTypes, declaredTypes, aliases));
        }

        return entries;
    }

    private static string[] ReadIdArray(BinaryReader reader, string[] strings, long fileLength)
    {
        var count = reader.Read7BitEncodedInt();
        if (count == 0)
            return [];
        if (count < 0 || count > fileLength)
            throw new InvalidDataException("token index list is out of range");
        var items = new string[count];
        for (var i = 0; i < count; i++)
            items[i] = StringAt(strings, reader.Read7BitEncodedInt());
        return items;
    }

    private static string StringAt(string[] strings, int id)
    {
        if ((uint)id >= (uint)strings.Length)
            throw new InvalidDataException("token index entry names a string outside its table");
        return strings[id];
    }

    private static FileStream? AcquireWriteLock(string directory)
    {
        var path = Path.Combine(directory, "write.lock");
        for (var attempt = 0; attempt < WriteLockAttempts; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (File.Exists(path))
            {
                Thread.Sleep(WriteLockRetryMs);
            }
        }

        return null;
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record ScanResult(
        string[] Tokens,
        string[] BaseTypes,
        string[] DeclaredTypes,
        (string Alias, string Target)[] GlobalAliases);

    private sealed record DocEntry(
        RepoPath Path,
        string PreprocessorKey,
        long Length,
        long WrittenTicks,
        string Checksum,
        string[] Tokens,
        string[] BaseTypes,
        string[] DeclaredTypes,
        (string Alias, string Target)[] GlobalAliases);

    private sealed record ScannedEntry(
        DocumentId DocumentId,
        VersionStamp Version,
        RepoPath Path,
        string PreprocessorKey,
        long Length,
        long WrittenTicks,
        string Checksum,
        ScanResult Scan);
}
