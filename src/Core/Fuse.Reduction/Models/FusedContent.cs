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
    /// <param name="redactionCounts">Per-kind redaction counts when secret redaction was applied.</param>
    /// <param name="inclusionChain">Optional dependency inclusion chain for provenance annotations.</param>
    /// <param name="relevanceScore">Optional relevance score assigned by scoping, used to order emission.</param>
    public FusedContent(
        SourceFile sourceFile,
        string content,
        ITokenCounter tokenCounter,
        IReadOnlyDictionary<string, int>? redactionCounts = null,
        IReadOnlyList<string>? inclusionChain = null,
        double? relevanceScore = null)
    {
        SourceFile = sourceFile;
        Content = content;
        NormalizedPath = sourceFile.NormalizedRelativePath;
        TokenCount = tokenCounter.Count(content);
        IsTrivial = ComputeIsTrivial(content);
        RedactionCounts = redactionCounts;
        InclusionChain = inclusionChain;
        RelevanceScore = relevanceScore;
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

    /// <summary>
    ///     Gets per-kind redaction counts when secret redaction was applied.
    /// </summary>
    public IReadOnlyDictionary<string, int>? RedactionCounts { get; }

    /// <summary>
    ///     Gets the dependency inclusion chain when provenance tracking is enabled.
    /// </summary>
    public IReadOnlyList<string>? InclusionChain { get; }

    /// <summary>
    ///     Gets the relevance score assigned by scoping, or <c>null</c> when the run was not scoped. Higher is
    ///     more relevant. Emission uses this to order entries so the most relevant survive a token budget.
    /// </summary>
    public double? RelevanceScore { get; }

    /// <summary>
    ///     Returns a copy of this content with the specified inclusion chain.
    /// </summary>
    /// <param name="inclusionChain">The dependency inclusion chain to attach for provenance annotations.</param>
    /// <returns>
    ///     A new <see cref="FusedContent" /> with identical content and the supplied
    ///     <see cref="InclusionChain" />; the existing <see cref="TokenCount" /> is preserved rather than
    ///     recomputed.
    /// </returns>
    public FusedContent WithInclusionChain(IReadOnlyList<string> inclusionChain) =>
        new(SourceFile, Content, new StaticTokenCounter(TokenCount), RedactionCounts, inclusionChain, RelevanceScore);

    /// <summary>
    ///     Returns a copy of this content with the specified relevance score.
    /// </summary>
    /// <param name="relevanceScore">The relevance score to attach.</param>
    /// <returns>
    ///     A new <see cref="FusedContent" /> with identical content and the supplied
    ///     <see cref="RelevanceScore" />; the existing <see cref="TokenCount" /> is preserved rather than
    ///     recomputed.
    /// </returns>
    public FusedContent WithRelevanceScore(double relevanceScore) =>
        new(SourceFile, Content, new StaticTokenCounter(TokenCount), RedactionCounts, InclusionChain, relevanceScore);

    /// <summary>
    ///     Returns a copy of this content with replaced body text and a recomputed token count, preserving
    ///     redaction counts, inclusion chain, and relevance score.
    /// </summary>
    /// <param name="content">The new reduced content.</param>
    /// <param name="tokenCounter">The token counter used to recompute <see cref="TokenCount" />.</param>
    /// <returns>A new <see cref="FusedContent" /> with the supplied content.</returns>
    public FusedContent WithReducedContent(string content, ITokenCounter tokenCounter) =>
        new(SourceFile, content, tokenCounter, RedactionCounts, InclusionChain, RelevanceScore);

    private static bool ComputeIsTrivial(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return true;

        var trimmed = content.Trim();

        if (trimmed is "{}" or "[]")
            return true;

        return trimmed.Length <= 120 && SelfClosingXmlPattern.IsMatch(trimmed);
    }

    private sealed class StaticTokenCounter(int count) : ITokenCounter
    {
        public int Count(string content) => count;
    }
}
