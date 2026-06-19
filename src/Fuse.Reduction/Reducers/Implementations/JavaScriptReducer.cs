using System.Text.RegularExpressions;

using Fuse.Reduction.Options;



namespace Fuse.Reduction.Reducers.Implementations;



/// <summary>

///     Reduces JavaScript files by removing comments and unnecessary whitespace.

/// </summary>

public sealed class JavaScriptReducer : IContentReducer

{

    /// <inheritdoc />

    public string Extension => ".js";



    /// <inheritdoc />

    public string Reduce(string content, ReductionOptions options)

    {

        content = Regex.Replace(content, @"(?<!:)//(?!/)[^\r\n]*", string.Empty);

        content = Regex.Replace(content, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        content = Regex.Replace(content, @"(\r?\n){2,}", "\n");

        content = Regex.Replace(content, @"^\s+|\s+$", string.Empty, RegexOptions.Multiline);

        content = Regex.Replace(content, @" {2,}", " ");

        content = Regex.Replace(content, @"\s*([{}\[\]();,:])\s*", "$1");



        return content.Trim();

    }

}

