using System.Collections.Concurrent;
using Fuse.Reduction.Tokenization;

namespace Fuse.Emission.Tokenization;

/// <summary>
///     Creates and caches <see cref="ITokenCounter" /> instances by model name or encoding.
/// </summary>
public sealed class TokenizerFactory
{
    private readonly ConcurrentDictionary<string, ITokenCounter> _counters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     The default tokenizer encoding (<c>o200k_base</c>) used when no model is specified.
    /// </summary>
    public const string DefaultModel = "o200k_base";

    // Average characters per token used by the Anthropic and Gemini estimators. These are published
    // rules-of-thumb (roughly 3.5 and 4 characters per token for English and code), not exact vocabularies;
    // see ApproximateTokenCounter for why exact offline counting is not available for those families.
    private const double AnthropicCharsPerToken = 3.5;
    private const double GeminiCharsPerToken = 4.0;

    /// <summary>
    ///     Returns a cached token counter for the specified model name or encoding identifier.
    /// </summary>
    /// <param name="modelOrEncoding">
    ///     A model name (for example <c>gpt-4o</c>, <c>claude</c>, <c>gemini</c>) or Tiktoken encoding
    ///     identifier (for example <c>cl100k_base</c>), or <c>null</c> to use <see cref="DefaultModel" />.
    /// </param>
    /// <returns>
    ///     A token counter for the resolved family. OpenAI encodings are counted exactly with Tiktoken;
    ///     Anthropic and Gemini families use a deterministic estimator. Counters are cached and reused.
    /// </returns>
    /// <remarks>
    ///     Counters are keyed by the resolved family, so different model aliases that map to the same family
    ///     share a single instance. Safe for concurrent use.
    /// </remarks>
    public ITokenCounter GetCounter(string? modelOrEncoding = null)
    {
        var family = ResolveFamily(modelOrEncoding ?? DefaultModel);
        return _counters.GetOrAdd(family, CreateCounter);
    }

    private static ITokenCounter CreateCounter(string family) =>
        family switch
        {
            "anthropic" => new ApproximateTokenCounter(AnthropicCharsPerToken),
            "gemini" => new ApproximateTokenCounter(GeminiCharsPerToken),
            _ => new TikTokenCounter(family),
        };

    /// <summary>
    ///     Maps a model name or encoding identifier to a counter family key: <c>anthropic</c>, <c>gemini</c>,
    ///     or a Tiktoken encoding name.
    /// </summary>
    /// <param name="modelOrEncoding">The model name or encoding identifier to resolve.</param>
    /// <returns>The resolved family key used to select and cache a counter.</returns>
    public static string ResolveFamily(string modelOrEncoding)
    {
        var normalized = modelOrEncoding.Trim().ToLowerInvariant();
        if (normalized.StartsWith("claude", StringComparison.Ordinal) ||
            normalized.StartsWith("anthropic", StringComparison.Ordinal))
        {
            return "anthropic";
        }

        if (normalized.StartsWith("gemini", StringComparison.Ordinal) ||
            normalized.StartsWith("google", StringComparison.Ordinal))
        {
            return "gemini";
        }

        return ResolveEncoding(modelOrEncoding);
    }

    /// <summary>
    ///     Maps a model name or encoding identifier to a Tiktoken encoding name.
    /// </summary>
    /// <param name="modelOrEncoding">The model name or encoding identifier to resolve.</param>
    /// <returns>
    ///     The resolved Tiktoken encoding name. Known model aliases map to <c>o200k_base</c> or
    ///     <c>cl100k_base</c>; values already containing an underscore are treated as encoding names and
    ///     returned as-is; anything else falls back to <see cref="DefaultModel" />.
    /// </returns>
    public static string ResolveEncoding(string modelOrEncoding) =>
        modelOrEncoding.Trim().ToLowerInvariant() switch
        {
            "o200k_base" or "gpt-4o" or "gpt-4o-mini" or "gpt-4.1" or "gpt-4.1-mini" => "o200k_base",
            "cl100k_base" or "gpt-4" or "gpt-3.5-turbo" or "gpt-3.5" => "cl100k_base",
            _ when modelOrEncoding.Contains('_', StringComparison.Ordinal) => modelOrEncoding,
            _ => DefaultModel,
        };
}
