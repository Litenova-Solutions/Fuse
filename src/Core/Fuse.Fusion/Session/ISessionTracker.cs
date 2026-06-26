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
    ///     Records that a file's content is being emitted, returning whether it is new, changed, or unchanged for
    ///     the session, and the previously emitted content when it changed and that content was retained.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="path">The normalized, forward-slash relative path of the file.</param>
    /// <param name="contentHash">A hash of the file's emitted content.</param>
    /// <param name="content">The emitted content, retained (subject to a size cap) so a later change can be diffed.</param>
    /// <returns>
    ///     A <see cref="SessionClaim" /> whose status is <see cref="SessionEntryStatus.New" /> when the file was
    ///     not seen in this session, <see cref="SessionEntryStatus.Unchanged" /> when the identical content was
    ///     already emitted, or <see cref="SessionEntryStatus.Changed" /> when it was emitted with different
    ///     content; on a change the prior content is supplied when it was retained, else <see langword="null" />.
    /// </returns>
    SessionClaim Claim(string sessionId, string path, ulong contentHash, string content);

    /// <summary>
    ///     Clears all tracking for a session, so the next fusion re-sends everything.
    /// </summary>
    /// <param name="sessionId">The session identifier to reset.</param>
    void Reset(string sessionId);
}

/// <summary>
///     Whether a claimed file is new to the session, changed since it was last emitted, or unchanged.
/// </summary>
public enum SessionEntryStatus
{
    /// <summary>The file was not previously emitted in this session.</summary>
    New,

    /// <summary>The file was previously emitted in this session with different content.</summary>
    Changed,

    /// <summary>The identical content was already emitted in this session.</summary>
    Unchanged,
}

/// <summary>
///     The outcome of <see cref="ISessionTracker.Claim" />: the entry's status and, for a change, the prior
///     emitted content when it was retained.
/// </summary>
/// <param name="Status">Whether the file is new, changed, or unchanged for the session.</param>
/// <param name="PriorContent">
///     The previously emitted content when <paramref name="Status" /> is <see cref="SessionEntryStatus.Changed" />
///     and that content was retained; otherwise <see langword="null" />.
/// </param>
public readonly record struct SessionClaim(SessionEntryStatus Status, string? PriorContent);
