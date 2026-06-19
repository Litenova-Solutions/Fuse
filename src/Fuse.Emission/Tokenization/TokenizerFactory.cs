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
    ///     Default tokenizer model used when none is specified.
    /// </summary>
    public const string DefaultModel = "o200k_base";

    /// <summary>
    ///     Returns a token counter for the specified model name or encoding identifier.
    /// </summary>
    public ITokenCounter GetCounter(string? modelOrEncoding = null)
    {
        var encoding = ResolveEncoding(modelOrEncoding ?? DefaultModel);
        return _counters.GetOrAdd(encoding, static enc => new TikTokenCounter(enc));
    }

    /// <summary>
    ///     Maps a model name or encoding identifier to a Tiktoken encoding name.
    /// </summary>
    public static string ResolveEncoding(string modelOrEncoding) =>
        modelOrEncoding.Trim().ToLowerInvariant() switch
        {
            "o200k_base" or "gpt-4o" or "gpt-4o-mini" or "gpt-4.1" or "gpt-4.1-mini" => "o200k_base",
            "cl100k_base" or "gpt-4" or "gpt-3.5-turbo" or "gpt-3.5" => "cl100k_base",
            _ when modelOrEncoding.Contains('_', StringComparison.Ordinal) => modelOrEncoding,
            _ => DefaultModel,
        };
}
