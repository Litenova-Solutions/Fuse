using Fuse.Reduction.Tokenization;

namespace Fuse.Emission.Tokenization;

/// <summary>
///     A deterministic, model-family token estimator for providers that do not ship a local tokenizer.
/// </summary>
/// <remarks>
///     Anthropic (Claude) and Google (Gemini) do not publish a local tokenizer vocabulary for their current
///     models, so exact offline counting is not possible; both providers expose a token-counting API instead.
///     This counter approximates a sub-word tokenizer: each run of letters and digits costs
///     <c>ceil(length / charsPerToken)</c> tokens and each other non-whitespace character costs one token.
///     The result is an estimate for budgeting, not an exact count. Use an OpenAI encoding (<c>o200k_base</c>,
///     <c>cl100k_base</c>) for exact offline counts, or the provider API for an exact provider-specific count.
///     The estimate is deterministic and allocation-light, with no reflection.
/// </remarks>
public sealed class ApproximateTokenCounter : ITokenCounter
{
    private readonly double _charsPerToken;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ApproximateTokenCounter" /> class.
    /// </summary>
    /// <param name="charsPerToken">
    ///     The average number of characters per token for the target model family. Must be greater than zero.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="charsPerToken" /> is not positive.</exception>
    public ApproximateTokenCounter(double charsPerToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(charsPerToken, 0);
        _charsPerToken = charsPerToken;
    }

    /// <inheritdoc />
    public int Count(string content)
    {
        if (string.IsNullOrEmpty(content))
            return 0;

        var tokens = 0;
        var wordLength = 0;

        foreach (var c in content)
        {
            if (char.IsLetterOrDigit(c))
            {
                wordLength++;
                continue;
            }

            if (wordLength > 0)
            {
                tokens += WordTokens(wordLength);
                wordLength = 0;
            }

            if (!char.IsWhiteSpace(c))
                tokens++;
        }

        if (wordLength > 0)
            tokens += WordTokens(wordLength);

        return tokens;
    }

    private int WordTokens(int wordLength) =>
        (int)Math.Ceiling(wordLength / _charsPerToken);
}
