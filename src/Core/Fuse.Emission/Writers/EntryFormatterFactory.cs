using Fuse.Emission.Models;

namespace Fuse.Emission.Writers;

/// <summary>
///     Resolves <see cref="IEntryFormatter" /> instances for output formats.
/// </summary>
public static class EntryFormatterFactory
{
    /// <summary>
    ///     Creates an entry formatter for the specified output format.
    /// </summary>
    /// <param name="format">The output format to create a formatter for.</param>
    /// <returns>
    ///     A <see cref="MarkdownEntryFormatter" /> for <see cref="OutputFormat.Markdown" />, a
    ///     <see cref="JsonEntryFormatter" /> for <see cref="OutputFormat.Json" />, otherwise a
    ///     <see cref="XmlEntryFormatter" />.
    /// </returns>
    public static IEntryFormatter Create(OutputFormat format) =>
        format switch
        {
            OutputFormat.Markdown => new MarkdownEntryFormatter(),
            OutputFormat.Json => new JsonEntryFormatter(),
            OutputFormat.Compact => new CompactEntryFormatter(),
            _ => new XmlEntryFormatter(),
        };

    /// <summary>
    ///     Parses a format name from CLI or config input (case-insensitive).
    /// </summary>
    /// <param name="value">
    ///     The format name to parse. Accepts <c>xml</c>, <c>markdown</c>/<c>md</c>, and <c>json</c>; empty
    ///     or <c>null</c> resolves to <see cref="OutputFormat.Xml" />.
    /// </param>
    /// <returns>The parsed <see cref="OutputFormat" />.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value" /> is not a recognized format name.</exception>
    public static OutputFormat ParseFormat(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "markdown" or "md" => OutputFormat.Markdown,
            "json" => OutputFormat.Json,
            "compact" => OutputFormat.Compact,
            "xml" or "" or null => OutputFormat.Xml,
            _ => throw new ArgumentException($"Unknown output format '{value}'. Use xml, markdown, json, or compact."),
        };
}
