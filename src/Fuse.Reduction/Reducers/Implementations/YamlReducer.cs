using System.Text.RegularExpressions;

using Fuse.Reduction.Options;



namespace Fuse.Reduction.Reducers.Implementations;



/// <summary>

///     Reduces YAML files by removing comments and excessive blank lines.

/// </summary>

public sealed class YamlReducer : IContentReducer

{

    /// <inheritdoc />

    public string Extension => ".yaml";



    /// <inheritdoc />

    public string Reduce(string content, ReductionOptions options)

    {

        content = Regex.Replace(content, @"^\s*#.*$", string.Empty, RegexOptions.Multiline);

        content = Regex.Replace(content, @"[ \t]+$", string.Empty, RegexOptions.Multiline);

        content = Regex.Replace(content, @"(\r?\n){3,}", "\n\n");

        content = Regex.Replace(content, @"^[\r\n]+", string.Empty);

        content = Regex.Replace(content, @"[\r\n]+$", string.Empty);



        return content;

    }

}

