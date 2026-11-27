namespace Fuse.Operations;

/// <summary>
///     What parsing a command's arguments gives: the parsed value, or the message that refuses them. A caller switches
///     over the two cases, so a refusal can never be read as a value.
/// </summary>
/// <typeparam name="T">The parsed arguments.</typeparam>
internal abstract record ParseResult<T>
{
    private ParseResult()
    {
    }

    /// <summary>The arguments parsed.</summary>
    /// <param name="Value">The parsed arguments.</param>
    public sealed record Parsed(T Value) : ParseResult<T>;

    /// <summary>The arguments are refused.</summary>
    /// <param name="Message">The line to print, starting with <c>fuse:</c>.</param>
    public sealed record Refused(string Message) : ParseResult<T>;
}
