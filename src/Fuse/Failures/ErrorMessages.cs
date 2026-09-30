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
    ///     The message for <see cref="ErrorCode.NoProjects"/>, without the <c>fuse:</c> prefix: the client, the engine and
    ///     <c>fuse init</c> print it when git knows no <c>.csproj</c> in the repository.
    /// </summary>
    public const string NoProjects = "no C# projects (.csproj) in this repository, so Fuse has nothing to check";

    /// <summary>
    ///     The message for <see cref="ErrorCode.InvalidPath"/> when a check names a file by an empty or blank string. The
    ///     command line and the MCP server refuse such an argument before they send anything, and the engine refuses such
    ///     a request line.
    /// </summary>
    public const string EmptyPath = "a file named in the check is empty; name each file by its path";

    /// <summary>The message for <see cref="ErrorCode.InvalidPath"/> when a check names a file by a string that is not a path, such as one holding a NUL character.</summary>
    public static string InvalidPath(string file) => $"\"{file}\" named in the check is not a valid path";

    /// <summary>
    ///     The message for <see cref="ErrorCode.InvalidPath"/> when none of the files a check names is a C# source file of
    ///     a project, such as a path with a typo or a file of another language. The engine answers with it instead of
    ///     checking nothing, which would read as no errors introduced.
    /// </summary>
    public static string NothingToCheck(IReadOnlyList<string> files) =>
        $"none of the files named in the check is a C# source file of a project in this repository: {string.Join(", ", files.Take(5))}{(files.Count > 5 ? $" and {files.Count - 5} more" : "")}";
}
