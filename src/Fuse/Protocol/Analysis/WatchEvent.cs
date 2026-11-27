using Fuse.Protocol.Check;
namespace Fuse.Protocol.Analysis;

/// <summary>
///     One event streamed by <c>fuse watch --all --json</c> or <c>fuse watch --session &lt;name&gt;</c> over the
///     engine pipe as checks finish or frozen contract surfaces change.
/// </summary>
/// <param name="EventType">Event kind (<c>check</c>, <c>session</c>, <c>guard_violation</c>, <c>reload</c>, <c>heartbeat</c>).</param>
/// <param name="Session">Session name associated with the event, or null for repository-wide events.</param>
/// <param name="WorktreeRoot">Worktree root path that originated the event, or null.</param>
/// <param name="Generation">Workspace generation counter.</param>
/// <param name="Tree">Tree stamp: 12 lowercase hex characters, or empty.</param>
/// <param name="ErrorCount">Introduced error count reported by the event.</param>
/// <param name="Errors">Errors included in this event.</param>
/// <param name="GuardName">Name of the freeze guard that fired, or null.</param>
/// <param name="ViolatedPattern">Frozen path pattern that was modified, or null.</param>
/// <param name="Declaration">Changed declaration header on a guard violation, or null.</param>
/// <param name="Message">Human-readable summary line for the event.</param>
internal sealed record WatchEvent(
    string EventType,
    string? Session = null,
    string? WorktreeRoot = null,
    long Generation = 0,
    string Tree = "",
    int ErrorCount = 0,
    ReportedError[]? Errors = null,
    string? GuardName = null,
    string? ViolatedPattern = null,
    string? Declaration = null,
    string? Message = null)
{
    /// <summary>Tree stamp: 12 lowercase hex characters, or empty.</summary>
    public string Tree { get; init; } = Tree ?? "";

    /// <summary>Errors included in this event.</summary>
    public ReportedError[] Errors { get; init; } = Errors ?? [];

    /// <summary>UTC timestamp when the event was emitted.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
