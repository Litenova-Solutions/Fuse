using Fuse.Plugins.Abstractions.Options;
using Fuse.Plugins.Abstractions.Reducers;
using System.Text.RegularExpressions;

namespace Fuse.Plugins.Formats.Web.Reducers;

/// <summary>
///     Reduces SQL files by removing line and block comments and collapsing blank lines and trailing
///     whitespace. Statement structure and string literals are left intact.
/// </summary>
/// <remarks>
///     Comment stripping is lexical, not a full SQL parse: a <c>--</c> or <c>/* */</c> sequence inside a string
///     literal is rare in practice but would be removed, so the reducer favors token savings over guaranteeing
///     executable output, consistent with the other format reducers.
/// </remarks>
public sealed partial class SqlReducer : IContentReducer
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".sql"];

    /// <inheritdoc />
    public string Reduce(string content, ReductionOptions options)
    {
        content = LineCommentRegex().Replace(content, string.Empty);
        content = BlockCommentRegex().Replace(content, string.Empty);
        content = TrimLineWhitespaceRegex().Replace(content, string.Empty);
        content = BlankLineRegex().Replace(content, "\n");
        content = MultiSpaceRegex().Replace(content, " ");
        return content.Trim();
    }

    [GeneratedRegex(@"--[^\r\n]*", RegexOptions.Compiled)]
    private static partial Regex LineCommentRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex BlockCommentRegex();

    [GeneratedRegex(@"^[ \t]+|[ \t]+$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex TrimLineWhitespaceRegex();

    [GeneratedRegex(@"(\r?\n){2,}", RegexOptions.Compiled)]
    private static partial Regex BlankLineRegex();

    [GeneratedRegex(@"[ \t]{2,}", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();
}
