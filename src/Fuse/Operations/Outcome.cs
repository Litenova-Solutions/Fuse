namespace Fuse.Operations;

/// <summary>
///     How an operation ended. It decides the exit code, whether the MCP server marks the result as an error, and
///     whether a hook wakes the agent.
/// </summary>
internal enum Outcome
{
    /// <summary>Fuse answered and found nothing to fix: no errors introduced, the tests passed, or the build succeeded.</summary>
    Clean,

    /// <summary>Fuse answered and found something to fix: errors introduced, a test failed, or the build failed.</summary>
    ProblemsFound,

    /// <summary>
    ///     Fuse could not answer: the engine was still loading, a restore is needed, the engine did not answer in time,
    ///     or an internal error ended the request.
    /// </summary>
    Unanswered,
}
