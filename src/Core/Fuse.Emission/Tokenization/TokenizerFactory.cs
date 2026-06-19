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

    /// <summary>
    ///     Returns a cached token counter for the specified model name or encoding identifier.
    /// </summary>
    /// <param name="modelOrEncoding">
    ///     A model name (for example <c>gpt-4o</c>) or Tiktoken encoding identifier (for example
    ///     <c>cl100k_base</c>), or <c>null</c> to use <see cref="DefaultModel" />.
    /// </param>
    /// <returns>
    ///     A token counter for the resolved encoding. Counters are cached per encoding and reused across calls.
    /// </returns>
    /// <remarks>
    ///     Counters are keyed by the resolved encoding name, so different model aliases that map to the same
    ///     encoding share a single instance. Safe for concurrent use.
    /// </remarks>
    public ITokenCounter GetCounter(string? modelOrEncoding = null)
    {
        var encoding = ResolveEncoding(modelOrEncoding ?? DefaultModel);
        return _counters.GetOrAdd(encoding, static enc => new TikTokenCounter(enc));
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
