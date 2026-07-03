using System.Collections.Concurrent;
using Fuse.Indexing;
using Fuse.Semantics;

namespace Fuse.Retrieval;

/// <summary>
///     The speculative staging area (M1): an agent proposes a changeset (one or more single-file edits), Fuse
///     diagnoses it with the speculative typecheck (R1) and selects the tests that cover the changed symbols
///     (R5), and nothing touches the working tree until an explicit promote. The loop becomes
///     propose-oracle-commit: stage, diagnose, select, then promote the diff or discard it.
/// </summary>
/// <remarks>
///     Sessions are held in memory and keyed by an opaque id, so two candidate changesets over the same base are
///     isolated: staging into one never affects the other's edits or diagnostics. This first cut re-captures the
///     build per diagnose (no resident compilation yet), so diagnose latency tracks a build; the resident-
///     workspace fast path is future work. In-process test execution is deliberately out of scope (it moved to
///     M2): select returns the covering set for the agent to run with its own <c>dotnet test --filter</c>.
/// </remarks>
public sealed class ChangesetSessionStore
{
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    ///     Creates a changeset session rooted at a workspace directory.
    /// </summary>
    /// <param name="root">The absolute workspace root the session's edits are relative to.</param>
    /// <param name="id">An explicit session id, or null to generate one.</param>
    /// <returns>The new session's id.</returns>
    public string Create(string root, string? id = null)
    {
        var sessionId = id ?? Guid.NewGuid().ToString("N");
        _sessions[sessionId] = new SessionState(Path.GetFullPath(root));
        return sessionId;
    }

    /// <summary>
    ///     Stages (or replaces) a proposed edit to a file in a session. Nothing is written to disk.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="relativePath">The repo-relative path of the file being changed.</param>
    /// <param name="newContent">The proposed full new content of that file.</param>
    /// <returns><see langword="true" /> when the session exists and the edit was staged; otherwise <see langword="false" />.</returns>
    public bool Stage(string sessionId, string relativePath, string newContent)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
            return false;
        var key = Normalize(relativePath);
        lock (state.Gate)
            state.Edits[key] = newContent;
        return true;
    }

    /// <summary>Returns the repo-relative paths staged in a session, or null when the session is unknown.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The staged file paths, or null.</returns>
    public IReadOnlyList<string>? StagedFiles(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
            return null;
        lock (state.Gate)
            return state.Edits.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    ///     Speculatively typechecks each staged edit (R1), returning the diagnostics per file. Abstains per file
    ///     when tier-1 build capture is unavailable, never guessing green.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="buildTarget">The absolute solution or project path to build and capture.</param>
    /// <param name="client">The build-capture client that runs the speculative typecheck.</param>
    /// <param name="timeout">The per-file check timeout.</param>
    /// <param name="cancellationToken">A token to cancel the diagnose.</param>
    /// <returns>The per-file check results, or null when the session is unknown.</returns>
    public async Task<IReadOnlyList<ChangesetDiagnosis>?> DiagnoseAsync(
        string sessionId, string buildTarget, BuildCaptureClient client, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
            return null;
        List<KeyValuePair<string, string>> edits;
        lock (state.Gate)
            edits = state.Edits.ToList();

        var results = new List<ChangesetDiagnosis>(edits.Count);
        foreach (var (file, content) in edits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var check = await client.CheckAsync(buildTarget, file, content, timeout, cancellationToken);
            results.Add(new ChangesetDiagnosis(file, check));
        }

        return results;
    }

    /// <summary>
    ///     Selects the tests that cover the symbols in the session's changed files (R5's DI-resolved <c>tests</c>
    ///     edges), the small subset to run instead of the whole suite. Best-effort and bounded by R5 edge
    ///     completeness, never "all the tests".
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="store">The workspace index store.</param>
    /// <param name="explorer">The graph explorer that resolves covering tests.</param>
    /// <param name="limit">The maximum tests to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The covering test file paths (deduped), or null when the session is unknown.</returns>
    public async Task<IReadOnlyList<string>?> SelectCoveringTestsAsync(
        string sessionId, IWorkspaceIndexStore store, GraphNeighborhoodExplorer explorer, int limit, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
            return null;
        List<string> files;
        lock (state.Gate)
            files = state.Edits.Keys.ToList();

        var covering = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            // The changed file's declared types are the symbols whose covering tests we select.
            foreach (var node in await store.GetNodesByFileAsync(file, cancellationToken))
            {
                foreach (var test in await explorer.CoveringTestsAsync(node.DisplayName, limit, cancellationToken))
                    covering.Add(test.Path);
            }
        }

        return covering.OrderBy(p => p, StringComparer.Ordinal).Take(limit).ToList();
    }

    /// <summary>
    ///     Promotes a session: writes every staged edit to the working tree, then removes the session. This is
    ///     the only operation that touches disk, and only on an explicit call.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="cancellationToken">A token to cancel the writes.</param>
    /// <returns>The repo-relative paths written, or null when the session is unknown.</returns>
    public async Task<IReadOnlyList<string>?> PromoteAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!_sessions.TryRemove(sessionId, out var state))
            return null;
        List<KeyValuePair<string, string>> edits;
        lock (state.Gate)
            edits = state.Edits.ToList();

        var written = new List<string>(edits.Count);
        foreach (var (file, content) in edits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(Path.Combine(state.Root, file));
            var dir = Path.GetDirectoryName(full);
            if (dir is not null)
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(full, content, cancellationToken);
            written.Add(file);
        }

        return written.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    /// <summary>Discards a session, leaving the working tree untouched.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns><see langword="true" /> when a session was discarded; otherwise <see langword="false" />.</returns>
    public bool Discard(string sessionId) => _sessions.TryRemove(sessionId, out _);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('.', '/');

    // One session's mutable state. The gate serializes edits to the dictionary; sessions are independent, so two
    // changesets over the same base never share edits or diagnostics (the isolation the M1 tests pin).
    private sealed class SessionState(string root)
    {
        public string Root { get; } = root;
        public Dictionary<string, string> Edits { get; } = new(StringComparer.Ordinal);
        public object Gate { get; } = new();
    }
}

/// <summary>
///     The speculative-typecheck outcome for one staged file in a changeset session (M1).
/// </summary>
/// <param name="File">The repo-relative path of the staged file.</param>
/// <param name="Check">The speculative typecheck result (verified diagnostics, or an abstention).</param>
public sealed record ChangesetDiagnosis(string File, CheckResult Check);
