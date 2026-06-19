using System.Text.RegularExpressions;

using Fuse.Reduction.Options;



namespace Fuse.Reduction.Reducers.Implementations;



/// <summary>

///     Reduces Markdown files by removing decorative noise while preserving structure.

/// </summary>

public sealed class MarkdownReducer : IContentReducer

{

    /// <inheritdoc />

    public string Extension => ".md";



    /// <inheritdoc />

    public string Reduce(string content, ReductionOptions options)

    {

        content = Regex.Replace(content, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);

        content = Regex.Replace(content, @"(?m)^(?<text>.+)(\r?\n)(?<underline>[=\-]{3,})\s*$", m =>

        {

            var text = m.Groups["text"].Value.Trim();

            var underline = m.Groups["underline"].Value;

            var level = underline.StartsWith('=') ? "#" : "##";

            return $"{level} {text}";

        });

        content = Regex.Replace(content, @"(?m)^\s*[-*_]{3,}\s*(\r?\n)?", string.Empty);

        content = Regex.Replace(content, @"\s*\|\s*", "|");

        content = Regex.Replace(content, @"\[([^\]]+)\]\(([^\s)]+)\s+""[^""]*""\)", "[$1]($2)");

        content = Regex.Replace(content, @"(\r?\n){3,}", "\n\n");

        content = Regex.Replace(content, @"(?<!  )[ \t]+$", string.Empty, RegexOptions.Multiline);

        content = Regex.Replace(content, @"^[\r\n]+", string.Empty);

        content = Regex.Replace(content, @"[\r\n]+$", string.Empty);



        return content;

    }

}

