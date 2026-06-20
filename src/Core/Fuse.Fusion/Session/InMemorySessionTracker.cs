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
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ulong>> _sessions = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool TryClaim(string sessionId, string path, ulong contentHash)
    {
        var session = _sessions.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, ulong>(StringComparer.OrdinalIgnoreCase));

        var isNew = true;
        session.AddOrUpdate(
            path,
            _ => contentHash,
            (_, existing) =>
            {
                isNew = existing != contentHash;
                return contentHash;
            });

        return isNew;
    }

    /// <inheritdoc />
    public void Reset(string sessionId) => _sessions.TryRemove(sessionId, out _);
}
