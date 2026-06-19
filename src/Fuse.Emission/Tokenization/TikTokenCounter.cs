using Fuse.Reduction.Tokenization;
using TiktokenSharp;

namespace Fuse.Emission.Tokenization;

/// <summary>
///     Counts tokens using the TikToken <c>cl100k_base</c> encoding used by GPT-4 and GPT-3.5-turbo.
/// </summary>
public sealed class TikTokenCounter : ITokenCounter
{
    private static readonly Lazy<TikTokenCounter> LazyInstance = new(() => new TikTokenCounter());

    private readonly TikToken _tokenizer;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TikTokenCounter" /> class.
    /// </summary>
    private TikTokenCounter()
    {
        _tokenizer = TikToken.GetEncoding("cl100k_base");
    }

    /// <summary>
    ///     Gets the shared singleton instance.
    /// </summary>
    public static TikTokenCounter Instance => LazyInstance.Value;

    /// <inheritdoc />
    public int Count(string content)
    {
        return _tokenizer.Encode(content).Count;
    }
}
