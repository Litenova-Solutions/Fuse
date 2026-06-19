using System.Text.RegularExpressions;

using Fuse.Reduction.Options;



namespace Fuse.Reduction.Reducers.Implementations;



/// <summary>

///     Reduces HTML files by removing comments and unnecessary whitespace.

/// </summary>

public sealed class HtmlReducer : IContentReducer

{

    /// <inheritdoc />

    public string Extension => ".html";



    /// <inheritdoc />

    public string Reduce(string content, ReductionOptions options)

    {

        content = Regex.Replace(content, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);

        content = Regex.Replace(content, @">\s+<", "><");

        content = Regex.Replace(content, @"(\S+)=""([^""\s]+)""", m =>

        {

            var attrValue = m.Groups[2].Value;



            if (Regex.IsMatch(attrValue, @"[<>&'""]"))

                return m.Value;



            return $"{m.Groups[1].Value}={attrValue}";

        });

        content = Regex.Replace(content, @" {2,}", " ");

        content = Regex.Replace(content, @"^\s+|\s+$", string.Empty, RegexOptions.Multiline);



        return content;

    }

}

