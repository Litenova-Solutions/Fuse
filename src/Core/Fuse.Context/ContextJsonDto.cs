using System.Text.Json.Serialization;

namespace Fuse.Context;

/// <summary>
///     The JSON shape of an emitted semantic context.
/// </summary>
/// <param name="Mode">The plan mode.</param>
/// <param name="Root">The workspace root, when set.</param>
/// <param name="ChangedSince">The git base ref for review plans, when set.</param>
/// <param name="Files">The number of files.</param>
/// <param name="EstimatedTokens">The total token count.</param>
/// <param name="Entries">The file entries.</param>
/// <param name="Notes">Warnings/notes.</param>
public sealed record ContextJsonDto(
    string Mode,
    string? Root,
    string? ChangedSince,
    int Files,
    int EstimatedTokens,
    IReadOnlyList<ContextFileDto> Entries,
    IReadOnlyList<string> Notes);

/// <summary>
///     The JSON shape of a single emitted file.
/// </summary>
/// <param name="Path">The file's normalized path.</param>
/// <param name="Role">The file's role.</param>
/// <param name="Tier">The render tier.</param>
/// <param name="Score">The retrieval score.</param>
/// <param name="Tokens">The token count.</param>
/// <param name="Provenance">The provenance chain.</param>
/// <param name="Content">The rendered content.</param>
/// <param name="Unchanged">Whether the file was elided as unchanged in the session.</param>
public sealed record ContextFileDto(
    string Path,
    string Role,
    string Tier,
    double Score,
    int Tokens,
    IReadOnlyList<string> Provenance,
    string Content,
    bool Unchanged = false);

/// <summary>
///     Source-generated serializer context for emitted semantic-context JSON.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(ContextJsonDto))]
internal partial class FuseContextJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
