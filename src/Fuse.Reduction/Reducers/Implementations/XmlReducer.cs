using System.Text.RegularExpressions;

using Fuse.Reduction.Options;



namespace Fuse.Reduction.Reducers.Implementations;



/// <summary>

///     Reduces XML files, including project and MSBuild files, by removing comments and whitespace.

/// </summary>

public sealed class XmlReducer : IContentReducer

{

    /// <inheritdoc />

    public string Extension => ".xml";



    /// <inheritdoc />

    public string Reduce(string content, ReductionOptions options)

    {

        content = Regex.Replace(content, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);

        content = Regex.Replace(content, @">\s+<", "><");

        content = Regex.Replace(content, @"^\s+", string.Empty, RegexOptions.Multiline);

        content = Regex.Replace(content, @"\s+$", string.Empty, RegexOptions.Multiline);

        content = Regex.Replace(content, @"(\?>\s*)", "?>\n");

        content = Regex.Replace(content, @"[\r\n]+(?!<\?)", string.Empty);

        content = Regex.Replace(content, @" {2,}", " ");



        return content.Trim();

    }

}

