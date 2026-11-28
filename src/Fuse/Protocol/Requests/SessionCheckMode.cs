namespace Fuse.Protocol.Requests;

/// <summary>
///     Whether a check of every change only answers, or also marks its session stopped. A subagent that has stopped
///     writes no more, so from then on its parent session counts its edits as its own.
/// </summary>
internal enum SessionCheckMode
{
    /// <summary>Answers the errors the session's edits cause, leaving the session running.</summary>
    Check = 0,

    /// <summary>Answers the errors and marks the session stopped, as the stop hook sends.</summary>
    Stop = 1,
}
