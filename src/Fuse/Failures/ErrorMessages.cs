namespace Fuse.Failures;

/// <summary>
///     The wording of each failure whose message does not depend on the request, written once so that every command
///     that reports it says the same thing.
/// </summary>
internal static class ErrorMessages
{
    /// <summary>
    ///     The message for <see cref="ErrorCode.NotARepository"/>, without the <c>fuse:</c> prefix the caller adds. The
    ///     command line, <c>fuse init</c> and the MCP server print it when their working directory is in no git repository.
    /// </summary>
    public const string NotARepository = "not inside a git repository; Fuse compares your changes with HEAD, so it needs one";
}
