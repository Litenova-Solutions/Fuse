using System.Collections.Concurrent;

namespace Fuse.Fusion.Session;

/// <summary>
///     Process-scoped <see cref="ISessionTracker" /> backed by in-memory dictionaries. Used by the MCP server,
///     where the server process is the session boundary.
/// </summary>
/// <remarks>
///     Thread-safe: the MCP server may dispatch tool calls concurrently. State is never persisted, so it is
///     lost when the process exits, which matches the intended lifetime of a serve session.
/// </remarks>
public sealed class InMemorySessionTracker : ISessionTracker
{
    // Content larger than this is not retained for diffing (only its hash is kept), so a session's memory stays
    // bounded; a later change to such a file falls back to a whole-file resend. The unified-diff generator caps
    // diffing at its own line limit anyway, so retaining very large bodies would not pay off.
    private const int MaxRetainedChars = 64 * 1024;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Entry>> _sessions = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public SessionClaim Claim(string sessionId, string path, ulong contentHash, string content)
    {
        var session = _sessions.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase));
        var retained = content.Length <= MaxRetainedChars ? content : null;

        var status = SessionEntryStatus.New;
        string? prior = null;
        session.AddOrUpdate(
            path,
            _ => new Entry(contentHash, retained),
            (_, existing) =>
            {
                if (existing.Hash == contentHash)
                {
                    status = SessionEntryStatus.Unchanged;
                    return existing; // keep the retained content as-is
                }

                status = SessionEntryStatus.Changed;
                prior = existing.Content;
                return new Entry(contentHash, retained);
            });

        return new SessionClaim(status, prior);
    }

    /// <inheritdoc />
    public void Reset(string sessionId) => _sessions.TryRemove(sessionId, out _);

    private sealed record Entry(ulong Hash, string? Content);
}
