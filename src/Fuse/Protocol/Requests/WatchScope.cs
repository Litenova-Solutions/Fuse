using System.Text.Json.Serialization;
namespace Fuse.Protocol.Requests;

/// <summary>
///     Which sessions a watch stream reads: every session, or one named session. The JSON names the case in a
///     <c>scope</c> property, which System.Text.Json reads to choose the record to create.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "scope")]
[JsonDerivedType(typeof(AllSessions), nameof(AllSessions))]
[JsonDerivedType(typeof(OneSession), nameof(OneSession))]
internal abstract record WatchScope
{
    private WatchScope()
    {
    }

    /// <summary>Streams events for every session, worktree, and guard violation.</summary>
    public sealed record AllSessions : WatchScope;

    /// <summary>Streams the events of one session.</summary>
    /// <param name="Session">Session to filter to.</param>
    public sealed record OneSession(string Session) : WatchScope
    {
        /// <summary>Session to filter to.</summary>
        public string Session { get; init; } = Session ?? "";
    }
}
