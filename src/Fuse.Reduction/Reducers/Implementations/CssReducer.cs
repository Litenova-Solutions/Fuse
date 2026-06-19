using System.Text.RegularExpressions;

using Fuse.Reduction.Options;



namespace Fuse.Reduction.Reducers.Implementations;



/// <summary>

///     Reduces CSS files by removing comments and unnecessary whitespace.

/// </summary>

public sealed class CssReducer : IContentReducer

{

    /// <inheritdoc />

    public string Extension => ".css";



    /// <inheritdoc />

    public string Reduce(string content, ReductionOptions options)

    {

        content = Regex.Replace(content, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        content = Regex.Replace(content, @"[\r\n]+", string.Empty);

        content = Regex.Replace(content, @"\s*\{\s*", "{");

        content = Regex.Replace(content, @"\s*\}\s*", "}");

        content = Regex.Replace(content, @"\s*:\s*", ":");

        content = Regex.Replace(content, @"\s*;\s*", ";");

        content = Regex.Replace(content, @"\s*,\s*", ",");

        content = Regex.Replace(content, @" {2,}", " ");



        return content.Trim();

    }

}

