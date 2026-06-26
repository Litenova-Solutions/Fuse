using System.Text;
using System.Text.Json;
using Fuse.Retrieval;

namespace Fuse.Context;

/// <summary>
///     Emits a rendered context as XML, Markdown, or JSON: the semantic manifest preamble followed by each
///     file's provenance and rendered body.
/// </summary>
/// <remarks>
///     The token budget is honored upstream by the plan packing, so the emitter writes exactly the files the
///     plan kept; redaction is applied by the renderer before this point. JSON uses a source-generated
///     serializer context, per the project's reflection-free serialization invariant.
/// </remarks>
public static class SemanticContextEmitter
{
    /// <summary>
    ///     Emits the rendered context in the requested format.
    /// </summary>
    /// <param name="plan">The context plan (for the manifest).</param>
    /// <param name="rendered">The rendered files.</param>
    /// <param name="format">The output format.</param>
    /// <param name="root">The workspace root, for the manifest.</param>
    /// <param name="changedSince">The git base ref for review plans, for the manifest.</param>
    /// <returns>The emitted payload.</returns>
    public static string Emit(
        ContextPlan plan,
        RenderedContext rendered,
        ContextOutputFormat format,
        string? root = null,
        string? changedSince = null) => format switch
        {
            ContextOutputFormat.Markdown => EmitMarkdown(plan, rendered, root, changedSince),
            ContextOutputFormat.Json => EmitJson(plan, rendered, root, changedSince),
            _ => EmitXml(plan, rendered, root, changedSince),
        };

    private static string EmitXml(ContextPlan plan, RenderedContext rendered, string? root, string? changedSince)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!--");
        builder.Append(SemanticManifestBuilder.Build(plan, root, changedSince));
        builder.AppendLine("-->");

        foreach (var file in rendered.Files)
        {
            builder.AppendLine(
                $"<file path=\"{EscapeAttribute(file.Path)}\" role=\"{file.Role}\" tier=\"{file.Tier}\" tokens=\"{file.TokenCount}\">");
            var provenance = ProvenanceFormatter.Format(file.Provenance);
            if (provenance.Length > 0)
            {
                builder.AppendLine("<!--");
                builder.AppendLine(provenance);
                builder.AppendLine("-->");
            }

            builder.AppendLine(file.Content);
            builder.AppendLine("</file>");
        }

        return builder.ToString();
    }

    private static string EmitMarkdown(ContextPlan plan, RenderedContext rendered, string? root, string? changedSince)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Fuse semantic context");
        builder.AppendLine();
        builder.AppendLine("```");
        builder.Append(SemanticManifestBuilder.Build(plan, root, changedSince));
        builder.AppendLine("```");
        builder.AppendLine();

        foreach (var file in rendered.Files)
        {
            builder.AppendLine($"## {file.Path}  [{file.Role}/{file.Tier}]");
            var summary = ProvenanceFormatter.Summarize(file.Provenance);
            if (summary != "seed")
                builder.AppendLine($"_included via {summary}_");
            builder.AppendLine();
            builder.AppendLine("```csharp");
            builder.AppendLine(file.Content);
            builder.AppendLine("```");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string EmitJson(ContextPlan plan, RenderedContext rendered, string? root, string? changedSince)
    {
        var dto = new ContextJsonDto(
            Mode: plan.Mode,
            Root: root,
            ChangedSince: changedSince,
            Files: rendered.Files.Count,
            EstimatedTokens: rendered.TotalTokens,
            Entries: rendered.Files
                .Select(f => new ContextFileDto(f.Path, f.Role, f.Tier.ToString(), f.Score, f.TokenCount, f.Provenance, f.Content))
                .ToList(),
            Notes: plan.Warnings);

        return JsonSerializer.Serialize(dto, FuseContextJsonContext.Default.ContextJsonDto);
    }

    private static string EscapeAttribute(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);
}

/// <summary>The output format for an emitted semantic context.</summary>
public enum ContextOutputFormat
{
    /// <summary>An XML envelope with a manifest comment and file elements.</summary>
    Xml,

    /// <summary>A Markdown document with a manifest block and fenced file sections.</summary>
    Markdown,

    /// <summary>A JSON document.</summary>
    Json,
}
