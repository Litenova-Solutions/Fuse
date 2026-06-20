using Fuse.Reduction.Tokenization;
using Microsoft.ML.Tokenizers;

namespace Fuse.Emission.Tokenization;

/// <summary>
///     Counts tokens using OpenAI-compatible Tiktoken encodings via Microsoft.ML.Tokenizers.
/// </summary>
public sealed class TikTokenCounter : ITokenCounter
{
    private static readonly Lazy<TikTokenCounter> DefaultInstance =
        new(() => new TikTokenCounter(TokenizerFactory.DefaultModel));

    private readonly Tokenizer _tokenizer;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TikTokenCounter" /> class.
    /// </summary>
    /// <param name="modelOrEncoding">A model name or Tiktoken encoding identifier.</param>
    public TikTokenCounter(string modelOrEncoding)
    {
        var encoding = TokenizerFactory.ResolveEncoding(modelOrEncoding);
        _tokenizer = TiktokenTokenizer.CreateForEncoding(encoding);
    }

    /// <summary>
    ///     Gets the shared singleton using the default <c>o200k_base</c> encoding.
    /// </summary>
    public static TikTokenCounter Instance => DefaultInstance.Value;

    /// <inheritdoc />
    public int Count(string content) => _tokenizer.CountTokens(content);
}
