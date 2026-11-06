namespace Fuse.Evals;

/// <summary>
///     One caller in a concurrency wave: what it asked for, from which session, what the client waited, and how the
///     answer ended. The queue and engine times of its request are read from the engine's phase lines for the wave,
///     because several callers' lines arrive together and a line names its request, not its caller.
/// </summary>
/// <param name="Kind">The caller's form: <c>post-edit</c>, <c>scoped</c>, <c>stop</c> or <c>test-plan</c>.</param>
/// <param name="Session">The session the caller ran as, distinct from every other caller in the wave.</param>
/// <param name="File">The file the caller edited, relative to the repository root.</param>
/// <param name="WallMs">Wall time the client waited for the answer, in milliseconds.</param>
/// <param name="ExitCode">The fuse or hook process exit code.</param>
/// <param name="Errors">Introduced errors the answer reported; for a test plan, the tests that failed.</param>
/// <param name="TimedOut">Whether the answer says the engine did not answer in time.</param>
/// <param name="Busy">Whether the answer is a <c>Busy</c> answer.</param>
/// <param name="Unanswered">Whether Fuse could not answer: exit code 2 for a command, or a timed-out hook answer.</param>
internal sealed record ConcurrencyCall(
    string Kind,
    string Session,
    string File,
    double WallMs,
    int ExitCode,
    int Errors,
    bool TimedOut,
    bool Busy,
    bool Unanswered);
