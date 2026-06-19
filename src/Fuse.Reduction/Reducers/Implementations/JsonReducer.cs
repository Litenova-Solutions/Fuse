using System.Text.RegularExpressions;

using Fuse.Reduction.Options;



namespace Fuse.Reduction.Reducers.Implementations;



/// <summary>

///     Reduces JSON files by removing unnecessary whitespace.

/// </summary>

public sealed class JsonReducer : IContentReducer

{

    /// <inheritdoc />

    public string Extension => ".json";



    /// <inheritdoc />

    public string Reduce(string content, ReductionOptions options)

    {

        content = Regex.Replace(content, @"[\r\n]+", string.Empty);

        content = Regex.Replace(content, @":\s+", ":");

        content = Regex.Replace(content, @",\s+", ",");

        content = Regex.Replace(content, @"([\[{])\s+", "$1");

        content = Regex.Replace(content, @"\s+([\]}])", "$1");



        return content.Trim();

    }

}

