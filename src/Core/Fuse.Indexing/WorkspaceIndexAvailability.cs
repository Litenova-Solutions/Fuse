namespace Fuse.Indexing;

/// <summary>
///     Availability facts persisted in <c>index_meta</c> and reported by workspace status reads.
/// </summary>
/// <param name="FtsAvailable">Whether FTS5 full-text search was available when the store last initialized.</param>
public sealed record IndexAvailability(bool FtsAvailable)
{
    /// <summary>
    ///     The <c>index_meta</c> key under which init records whether FTS5 full-text search is available.
    /// </summary>
    public const string FtsAvailableMetaKey = "fts_available";

    /// <summary>Parses a persisted <see cref="FtsAvailableMetaKey" /> value.</summary>
    /// <param name="raw">The stored meta value, or null when the key is absent.</param>
    /// <returns><see langword="true" /> when FTS5 is available; otherwise <see langword="false" />.</returns>
    /// <remarks>
    ///     A pre-stamp index with no key is treated as available so legacy stores behave as before until the
    ///     next init writes the probe result.
    /// </remarks>
    public static bool ParseFtsMeta(string? raw) =>
        raw switch
        {
            "0" or "false" or "False" => false,
            "1" or "true" or "True" => true,
            null => true,
            _ => true,
        };

    /// <summary>Serializes an FTS availability probe for <see cref="FtsAvailableMetaKey" />.</summary>
    /// <param name="available">Whether FTS5 initialized successfully.</param>
    /// <returns><c>1</c> when available, <c>0</c> when not.</returns>
    public static string ToFtsMetaValue(bool available) => available ? "1" : "0";
}
