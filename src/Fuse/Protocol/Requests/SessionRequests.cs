using Fuse.Protocol.Analysis;
namespace Fuse.Protocol.Requests;

internal abstract partial record EngineRequest
{
    /// <summary>Registers an external writer as a session for the path patterns it writes.</summary>
    /// <param name="Name">The name the launcher gives the writer.</param>
    /// <param name="Paths">Repository-relative path patterns, as the launcher named them.</param>
    public sealed record BeginSession(string Name, string[] Paths) : EngineRequest
    {
        /// <summary>The name. A line without it reads as empty, which no registration holds.</summary>
        public string Name { get; init; } = Name ?? "";

        /// <summary>
        ///     The process the registration is tied to, or null for no process. The registration ends when that process
        ///     exits, checked as registrations are read; the 4-hour expiry stays as the fallback. A line without it, or
        ///     with a value that names no process, reads as null.
        /// </summary>
        private readonly int? _pid;

        public int? Pid
        {
            get => _pid;
            init => _pid = value is > 0 ? value : null;
        }

        /// <summary>The patterns. A line without the list reads as one that names none.</summary>
        public string[] Paths { get; init; } = Paths ?? [];
    }

    /// <summary>Ends the registration an earlier begin request made.</summary>
    /// <param name="Name">The name the launcher gave the writer.</param>
    public sealed record EndSession(string Name) : EngineRequest
    {
        /// <summary>The name. A line without it reads as empty, which no registration holds.</summary>
        public string Name { get; init; } = Name ?? "";
    }

    /// <summary>
    ///     Registers or removes a frozen contract surface guard (<c>fuse session guard</c>), answered with
    ///     <see cref="EngineResponse.Acknowledged"/>.
    /// </summary>
    public sealed record SessionGuard(FreezeGuardRequest Guard) : EngineRequest;

    /// <summary>
    ///     Subscribes to a server-push NDJSON stream of <see cref="EngineResponse.WatchEventPushed"/> messages for one
    ///     session or for every session and guard (<c>fuse watch --all</c>).
    /// </summary>
    public sealed record WatchStream : EngineRequest
    {
        /// <summary>Session to filter to, or null when <see cref="All"/> is true.</summary>
        public string? Session { get; init; }

        /// <summary>True to stream events for every session, worktree, and guard violation.</summary>
        public bool All { get; init; }
    }
}
