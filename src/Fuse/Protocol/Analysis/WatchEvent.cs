using System.Diagnostics;
using System.Text.Json.Serialization;
using Fuse.Protocol.Check;
namespace Fuse.Protocol.Analysis;

/// <summary>
///     One event streamed by <c>fuse watch --all --json</c> or <c>fuse watch --session &lt;name&gt;</c> over the
///     engine pipe as checks finish or frozen contract surfaces change. Each case carries only the fields its kind
///     reports; <see cref="Line"/> carries the one serialized shape every NDJSON line keeps.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CheckUpdated), nameof(CheckUpdated))]
[JsonDerivedType(typeof(SessionUpdated), nameof(SessionUpdated))]
[JsonDerivedType(typeof(GuardViolated), nameof(GuardViolated))]
[JsonDerivedType(typeof(Reloaded), nameof(Reloaded))]
[JsonDerivedType(typeof(Heartbeat), nameof(Heartbeat))]
[JsonDerivedType(typeof(Waiting), nameof(Waiting))]
internal abstract record WatchEvent
{
    private WatchEvent()
    {
    }

    /// <summary>Event kind (<c>check</c>, <c>session</c>, <c>guard_violation</c>, <c>reload</c>, <c>heartbeat</c> or <c>waiting</c>).</summary>
    public abstract string EventType { get; }

    /// <summary>Session name associated with the event, or null for repository-wide events.</summary>
    public abstract string? Session { get; init; }

    /// <summary>Human-readable summary line for the event, or null when the event has none.</summary>
    public abstract string? Message { get; init; }

    /// <summary>UTC timestamp when the event was emitted.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    ///     This event as the one serialized shape a <c>fuse watch --json</c> line prints, so every line keeps the
    ///     property names, order and omitted or null properties it always had.
    /// </summary>
    internal Line ToLine() => this switch
    {
        CheckUpdated updated => new Line("check", updated.Session, updated.WorktreeRoot, updated.Generation, updated.Tree, updated.ErrorCount, updated.Errors, Message: updated.Message) { Timestamp = Timestamp },
        SessionUpdated updated => new Line("session", updated.Session, Generation: updated.Generation, Tree: updated.Tree, ErrorCount: updated.ErrorCount, Errors: updated.Errors) { Timestamp = Timestamp },
        GuardViolated violated => new Line("guard_violation", violated.Session, violated.WorktreeRoot, violated.Generation, violated.Tree, GuardName: violated.GuardName, ViolatedPattern: violated.ViolatedPattern, Declaration: violated.Declaration, Message: violated.Message) { Timestamp = Timestamp },
        Reloaded reloaded => new Line("reload", reloaded.Session, reloaded.WorktreeRoot, Message: reloaded.Message) { Timestamp = Timestamp },
        Heartbeat heartbeat => new Line("heartbeat", heartbeat.Session, heartbeat.WorktreeRoot, Message: heartbeat.Message) { Timestamp = Timestamp },
        Waiting waiting => new Line("waiting", waiting.Session, Message: waiting.Message) { Timestamp = Timestamp },
        _ => throw new UnreachableException($"a watch event is a check, session, guard violation, reload, heartbeat or waiting event, not {GetType().Name}"),
    };

    /// <summary>A check answer the engine published for one session, with the introduced errors on the tree it names.</summary>
    public sealed record CheckUpdated : WatchEvent
    {
        private readonly string _tree = "";
        private readonly ReportedError[] _errors = [];

        /// <summary>Event kind (<c>check</c>).</summary>
        public override string EventType => "check";

        /// <summary>Session name associated with the event, or null for repository-wide events.</summary>
        public override string? Session { get; init; }

        /// <summary>Human-readable summary line for the event, or null when the event has none.</summary>
        public override string? Message { get; init; }

        /// <summary>Worktree root path that originated the event, or null.</summary>
        public string? WorktreeRoot { get; init; }

        /// <summary>Workspace generation counter.</summary>
        public long Generation { get; init; }

        /// <summary>Tree stamp: 12 lowercase hex characters, or empty.</summary>
        public string Tree
        {
            get => _tree;
            init => _tree = value ?? "";
        }

        /// <summary>Introduced error count reported by the event.</summary>
        public int ErrorCount { get; init; }

        /// <summary>Errors included in this event.</summary>
        public ReportedError[] Errors
        {
            get => _errors;
            init => _errors = value ?? [];
        }
    }

    /// <summary>A stored session answer read from the session files while no engine streams.</summary>
    public sealed record SessionUpdated : WatchEvent
    {
        private readonly string _tree = "";
        private readonly ReportedError[] _errors = [];

        /// <summary>Event kind (<c>session</c>).</summary>
        public override string EventType => "session";

        /// <summary>Session name associated with the event, or null for repository-wide events.</summary>
        public override string? Session { get; init; }

        /// <summary>Human-readable summary line for the event, or null when the event has none.</summary>
        public override string? Message { get; init; }

        /// <summary>Workspace generation counter.</summary>
        public long Generation { get; init; }

        /// <summary>Tree stamp: 12 lowercase hex characters, or empty.</summary>
        public string Tree
        {
            get => _tree;
            init => _tree = value ?? "";
        }

        /// <summary>Introduced error count reported by the event.</summary>
        public int ErrorCount { get; init; }

        /// <summary>Errors included in this event.</summary>
        public ReportedError[] Errors
        {
            get => _errors;
            init => _errors = value ?? [];
        }
    }

    /// <summary>A frozen declaration that changed under a guard's frozen pattern.</summary>
    public sealed record GuardViolated : WatchEvent
    {
        private readonly string _tree = "";

        /// <summary>Event kind (<c>guard_violation</c>).</summary>
        public override string EventType => "guard_violation";

        /// <summary>Session name associated with the event, or null for repository-wide events.</summary>
        public override string? Session { get; init; }

        /// <summary>Human-readable summary line for the event, or null when the event has none.</summary>
        public override string? Message { get; init; }

        /// <summary>Worktree root path that originated the event, or null.</summary>
        public string? WorktreeRoot { get; init; }

        /// <summary>Workspace generation counter.</summary>
        public long Generation { get; init; }

        /// <summary>Tree stamp: 12 lowercase hex characters, or empty.</summary>
        public string Tree
        {
            get => _tree;
            init => _tree = value ?? "";
        }

        /// <summary>Name of the freeze guard that fired, or null.</summary>
        public string? GuardName { get; init; }

        /// <summary>Frozen path pattern that was modified, or null.</summary>
        public string? ViolatedPattern { get; init; }

        /// <summary>Changed declaration header on a guard violation, or null.</summary>
        public string? Declaration { get; init; }
    }

    /// <summary>A repository-wide reload the engine reports to every subscription.</summary>
    public sealed record Reloaded : WatchEvent
    {
        /// <summary>Event kind (<c>reload</c>).</summary>
        public override string EventType => "reload";

        /// <summary>Session name associated with the event, or null for repository-wide events.</summary>
        public override string? Session { get; init; }

        /// <summary>Human-readable summary line for the event, or null when the event has none.</summary>
        public override string? Message { get; init; }

        /// <summary>Worktree root path that originated the event, or null.</summary>
        public string? WorktreeRoot { get; init; }
    }

    /// <summary>The first event of a watch subscription, which says the stream is up.</summary>
    public sealed record Heartbeat : WatchEvent
    {
        /// <summary>Event kind (<c>heartbeat</c>).</summary>
        public override string EventType => "heartbeat";

        /// <summary>Session name associated with the event, or null for repository-wide events.</summary>
        public override string? Session { get; init; }

        /// <summary>Human-readable summary line for the event, or null when the event has none.</summary>
        public override string? Message { get; init; }

        /// <summary>Worktree root path that originated the event, or null.</summary>
        public string? WorktreeRoot { get; init; }
    }

    /// <summary>A watch with no stored answer yet, which keeps waiting for the first one.</summary>
    public sealed record Waiting : WatchEvent
    {
        /// <summary>Event kind (<c>waiting</c>).</summary>
        public override string EventType => "waiting";

        /// <summary>Session name associated with the event, or null for repository-wide events.</summary>
        public override string? Session { get; init; }

        /// <summary>Human-readable summary line for the event, or null when the event has none.</summary>
        public override string? Message { get; init; }
    }

    /// <summary>
    ///     One serialized <c>fuse watch --json</c> line: the shape every NDJSON line keeps, whatever case the event is.
    /// </summary>
    /// <param name="EventType">Event kind (<c>check</c>, <c>session</c>, <c>guard_violation</c>, <c>reload</c>, <c>heartbeat</c> or <c>waiting</c>).</param>
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
    internal sealed record Line(
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
}
