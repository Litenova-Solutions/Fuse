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

    /// <summary>
    ///     The message for <see cref="ErrorCode.InvalidPath"/> when a check names a file by an empty or blank string. The
    ///     command line and the MCP server refuse such an argument before they send anything, and the engine refuses such
    ///     a request line.
    /// </summary>
    public const string EmptyPath = "a file named in the check is empty; name each file by its path";

    /// <summary>The message for <see cref="ErrorCode.InvalidPath"/> when a check names a file by a string that is not a path, such as one holding a NUL character.</summary>
    public static string InvalidPath(string file) => $"\"{file}\" named in the check is not a valid path";
}
