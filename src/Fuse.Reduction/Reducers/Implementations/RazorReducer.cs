using System.Text.RegularExpressions;

using Fuse.Reduction.Options;



namespace Fuse.Reduction.Reducers.Implementations;



/// <summary>

///     Reduces Razor and Blazor view files by removing comments and optimizing syntax.

/// </summary>

public sealed class RazorReducer : IContentReducer

{

    /// <inheritdoc />

    public string Extension => ".razor";



    /// <inheritdoc />

    public string Reduce(string content, ReductionOptions options)

    {

        content = Regex.Replace(content, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);

        content = Regex.Replace(content, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        content = Regex.Replace(content, @"(?<!:)//(?!/)[^\r\n]*", string.Empty);

        content = Regex.Replace(content, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline);

        content = Regex.Replace(content, @">\s+<", "><");

        content = Regex.Replace(content, @"@\(\s+", "@(");

        content = Regex.Replace(content, @"\s+\)", ")");

        content = Regex.Replace(content, @" {2,}", " ");

        content = Regex.Replace(content, @"^\s+|\s+$", string.Empty, RegexOptions.Multiline);

        content = Regex.Replace(content, @"(\r?\n){3,}", "\n\n");



        return content;

    }

}

