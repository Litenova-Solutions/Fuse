using Fuse.Languages.Abstractions.Reducers;
using Fuse.Languages.Abstractions.Options;
using System.Text.RegularExpressions;

namespace Fuse.Formats.Reducers;

/// <summary>
///     Reduces JSON files by removing unnecessary whitespace.
/// </summary>
public sealed partial class JsonReducer : IContentReducer
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".json"];

    /// <inheritdoc />
    public string Reduce(string content, ReductionOptions options)
    {
        content = NewlineRegex().Replace(content, string.Empty);
        content = ColonSpaceRegex().Replace(content, ":");
        content = CommaSpaceRegex().Replace(content, ",");
        content = OpenBracketBraceSpaceRegex().Replace(content, "$1");
        content = CloseBracketBraceSpaceRegex().Replace(content, "$1");
        return content.Trim();
    }

    [GeneratedRegex(@"[\r\n]+", RegexOptions.Compiled)]
    private static partial Regex NewlineRegex();

    [GeneratedRegex(@":\s+", RegexOptions.Compiled)]
    private static partial Regex ColonSpaceRegex();

    [GeneratedRegex(@",\s+", RegexOptions.Compiled)]
    private static partial Regex CommaSpaceRegex();

    [GeneratedRegex(@"([\[{])\s+", RegexOptions.Compiled)]
    private static partial Regex OpenBracketBraceSpaceRegex();

    [GeneratedRegex(@"\s+([\]}])", RegexOptions.Compiled)]
    private static partial Regex CloseBracketBraceSpaceRegex();
}
