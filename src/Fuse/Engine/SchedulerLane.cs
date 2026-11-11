namespace Fuse.Engine;

/// <summary>
///     A scheduler lane: the priority class a request waits in. Interactive hooks go first, then commands and MCP
///     calls, then background work, and within one lane requests run in arrival order.
/// </summary>
internal enum SchedulerLane
{
    /// <summary>Inline post-edit checks and stopping checks, which an agent waits on while it works.</summary>
    Interactive,

    /// <summary>Commands and MCP calls: checks, scoped checks and test plans a person or a host asked for.</summary>
    Command,

    /// <summary>Background checking and warming, which yields to every request a caller waits on.</summary>
    Background,
}
