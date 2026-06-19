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
    public static IEntryFormatter Create(OutputFormat format) =>
        format switch
        {
            OutputFormat.Markdown => new MarkdownEntryFormatter(),
            OutputFormat.Json => new JsonEntryFormatter(),
            _ => new XmlEntryFormatter(),
        };

    /// <summary>
    ///     Parses a format name from CLI or config (case-insensitive).
    /// </summary>
    public static OutputFormat ParseFormat(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "markdown" or "md" => OutputFormat.Markdown,
            "json" => OutputFormat.Json,
            "xml" or "" or null => OutputFormat.Xml,
            _ => throw new ArgumentException($"Unknown output format '{value}'. Use xml, markdown, or json."),
        };
}
