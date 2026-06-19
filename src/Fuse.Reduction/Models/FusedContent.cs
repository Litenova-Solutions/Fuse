using System.Text.RegularExpressions;

using Fuse.Collection.Models;

using Fuse.Reduction.Tokenization;



namespace Fuse.Reduction.Models;



/// <summary>

///     Represents reduced file content ready for emission.

/// </summary>

public sealed class FusedContent

{

    private static readonly Regex SelfClosingXmlPattern = new(

        @"^<[\w:.-]+(?:\s+[\w:.-]+(?:=""[^""]*""|'[^']*'|[^\s/>]+))*?\s*/>$",

        RegexOptions.Compiled | RegexOptions.CultureInvariant);



    /// <summary>

    ///     Initializes a new instance of the <see cref="FusedContent" /> class.

    /// </summary>

    /// <param name="sourceFile">The source file that produced this content.</param>

    /// <param name="content">The reduced file content.</param>

    /// <param name="tokenCounter">The token counter used to compute <see cref="TokenCount" />.</param>

    public FusedContent(SourceFile sourceFile, string content, ITokenCounter tokenCounter)

    {

        SourceFile = sourceFile;

        Content = content;

        NormalizedPath = sourceFile.NormalizedRelativePath;

        TokenCount = tokenCounter.Count(content);

        IsTrivial = ComputeIsTrivial(content);

    }



    /// <summary>

    ///     Gets the source file that produced this content.

    /// </summary>

    public SourceFile SourceFile { get; }



    /// <summary>

    ///     Gets the reduced file content.

    /// </summary>

    public string Content { get; }



    /// <summary>

    ///     Gets the token count for <see cref="Content" />.

    /// </summary>

    public int TokenCount { get; }



    /// <summary>

    ///     Gets a value indicating whether the content is semantically empty.

    /// </summary>

    /// <remarks>

    ///     Trivial content includes whitespace-only text, empty braces or brackets,

    ///     and short self-closing XML elements.

    /// </remarks>

    public bool IsTrivial { get; }



    /// <summary>

    ///     Gets the normalized relative path for the source file.

    /// </summary>

    public string NormalizedPath { get; }



    private static bool ComputeIsTrivial(string content)

    {

        if (string.IsNullOrWhiteSpace(content))

            return true;



        var trimmed = content.Trim();



        if (trimmed is "{}" or "[]")

            return true;



        return trimmed.Length <= 120 && SelfClosingXmlPattern.IsMatch(trimmed);

    }

}

