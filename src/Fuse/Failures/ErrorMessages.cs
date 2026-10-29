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
    /// <summary>
    ///     The message for <see cref="ErrorCode.InvalidPath"/> when none of the paths a test command names is a test
    ///     project, or holds one. The engine answers with it instead of running no test, which would read as a pass.
    /// </summary>
    public static string NoTestProjectNamed(IReadOnlyList<string> paths) =>
        $"none of the paths given names a test project in this repository: {string.Join(", ", paths.Take(5))}{(paths.Count > 5 ? $" and {paths.Count - 5} more" : "")}";

    public static string NoRegisteredSession(string name) => $"no registered session named {name}";

    /// <summary>The message for <see cref="ErrorCode.InvalidPath"/> when a session name holds a character a name cannot hold.</summary>
    public static string InvalidSessionName(string name) => $"\"{name}\" is not a valid session name; use 1 to 64 letters, digits, -, _ or .";

    /// <summary>The message for <see cref="ErrorCode.InvalidPath"/> when a session pattern is not a pattern the engine matches.</summary>
    public static string InvalidSessionPattern(string? pattern) => $"\"{pattern}\" is not a valid path pattern; use forward slashes, * and ? inside a segment, ** as a whole segment, no .. segment";

    /// <summary>The message for <see cref="ErrorCode.InvalidPath"/> when a begin request names no pattern.</summary>
    public static string SessionNeedsPattern() => "a session needs at least one path pattern; name one after --paths";

    /// <summary>The message for <see cref="ErrorCode.InvalidPath"/> when a registration names more patterns than one holds.</summary>
    public static string TooManySessionPatterns(int count) => $"a session holds at most 32 path patterns, not {count}; name fewer";

    /// <summary>The message for <see cref="ErrorCode.InvalidPath"/> when too many sessions are registered at once.</summary>
    public static string TooManyRegisteredSessions() => "at most 64 sessions can be registered at once; end one with fuse session end <name>";

    /// <summary>The message for <see cref="ErrorCode.InvalidPath"/> when a session pattern names a path outside the repository.</summary>
    public static string SessionPatternOutsideRepository(string pattern) => $"\"{pattern}\" is outside this repository; name a pattern inside it";

    /// <summary>The message for <see cref="ErrorCode.InvalidPath"/> when one directory or glob of a scoped check matches no changed source it could check.</summary>
    public static string ScopeMatchesNothing(string spec) => $"\"{spec}\" in the check matches no changed C# source file of a project in this repository";

    /// <summary>The message for <see cref="ErrorCode.InvalidPath"/> when the paths of a scoped check expand to more sources than one covers.</summary>
    public static string ScopeTooManyFiles(int count) => $"the paths named in the check expand to {count} source files, more than the 2000 a scoped check covers; name a smaller folder or pattern";

    public static string NothingToCheck(IReadOnlyList<string> files) =>
        $"none of the files named in the check is a C# source file of a project in this repository: {string.Join(", ", files.Take(5))}{(files.Count > 5 ? $" and {files.Count - 5} more" : "")}";
}
