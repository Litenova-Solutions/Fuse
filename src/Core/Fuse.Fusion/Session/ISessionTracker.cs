namespace Fuse.Fusion.Session;

/// <summary>
///     Tracks which file content has already been emitted within a session so that later fusions in the same
///     session can omit material the agent already holds.
/// </summary>
/// <remarks>
///     A session is identified by an opaque string. Tracking is keyed by session and normalized file path and
///     compares a content hash, so an unchanged file is omitted on a later call while a changed file is resent.
///     The default implementation is process-scoped and used by the MCP server, where a single long-running
///     process serves one agent across many calls.
/// </remarks>
public interface ISessionTracker
{
    /// <summary>
    ///     Records that a file's content is being emitted, returning whether it is new or changed for the session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="path">The normalized, forward-slash relative path of the file.</param>
    /// <param name="contentHash">A hash of the file's emitted content.</param>
    /// <returns>
    ///     <see langword="true" /> when the file has not been emitted in this session, or was emitted with
    ///     different content (the new hash is recorded); <see langword="false" /> when the identical content was
    ///     already emitted in this session.
    /// </returns>
    bool TryClaim(string sessionId, string path, ulong contentHash);

    /// <summary>
    ///     Clears all tracking for a session, so the next fusion re-sends everything.
    /// </summary>
    /// <param name="sessionId">The session identifier to reset.</param>
    void Reset(string sessionId);
}
