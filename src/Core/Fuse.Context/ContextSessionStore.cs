using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Text;

namespace Fuse.Context;

/// <summary>
///     Tracks which files have already been sent in a context session so unchanged files can be elided from
///     later payloads instead of resent. Used by the warm MCP server, where one agent makes many calls.
/// </summary>
/// <remarks>
///     A session maps a file path to the hash of the content last sent for it. <see cref="Reconcile" /> returns
///     the files whose content is unchanged since the previous send (so the caller can emit them as a brief
///     reference) and records the current content for every file. The store is in-memory and thread-safe.
/// </remarks>
public sealed class ContextSessionStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _sessions =
        new(StringComparer.Ordinal);

    /// <summary>
    ///     Records the current content of the rendered files for a session and returns the paths whose content
    ///     is unchanged since the previous call in that session.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="files">The rendered files about to be emitted.</param>
    /// <returns>The normalized paths that were already sent unchanged in this session.</returns>
    public IReadOnlyCollection<string> Reconcile(string sessionId, IReadOnlyList<RenderedFile> files)
    {
        var session = _sessions.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal));
        var unchanged = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var hash = Hash(file.Content);
            if (session.TryGetValue(file.Path, out var previous) && previous == hash)
                unchanged.Add(file.Path);
            session[file.Path] = hash;
        }

        return unchanged;
    }

    /// <summary>
    ///     Clears a session's record.
    /// </summary>
    /// <param name="sessionId">The session id to clear.</param>
    public void Clear(string sessionId) => _sessions.TryRemove(sessionId, out _);

    private static string Hash(string content) =>
        XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(content)).ToString("x16");
}
