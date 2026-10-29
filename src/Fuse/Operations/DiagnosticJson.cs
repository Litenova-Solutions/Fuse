using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fuse.Dotnet;
using Fuse.Protocol;

namespace Fuse.Operations;

/// <summary>One diagnostic as one JSON line, for <c>--json</c>: build and check share the shape.</summary>
internal static partial class DiagnosticJson
{
    // The lines go to a terminal or a script, never into HTML, so quotes and apostrophes stay as they are.
    private static readonly Lines Relaxed = new(new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    });

    /// <summary>One build diagnostic as one JSON object.</summary>
    /// <param name="lockedBy">The process holding the locked file, as <c>name (pid n)</c>, or null to leave the field out.</param>
    public static string BuildLine(BuildDiagnostic diagnostic, string? lockedBy = null) =>
        JsonSerializer.Serialize(
            new DiagnosticLine(diagnostic.File, diagnostic.Line, diagnostic.Column, diagnostic.Code, diagnostic.Message, LockedBy: lockedBy),
            Relaxed.DiagnosticLine);

    /// <summary>One introduced error as one JSON object.</summary>
    public static string CheckLine(ReportedError reported) =>
        JsonSerializer.Serialize(
            new DiagnosticLine(reported.Error.Path, reported.Error.Line, reported.Error.Column, reported.Error.Id, reported.Error.Message, CauseOf(reported.Cause), reported.GeneratedFile),
            Relaxed.DiagnosticLine);

    /// <summary>The cause as the text answer prints it under the error, or null for none.</summary>
    private static string? CauseOf(ReportedCause? cause) =>
        cause is null ? null : $"{(cause.Kind == CauseKind.Removed ? "removed" : "changed")}: {cause.Declaration}";

    /// <summary>One diagnostic line: the build fields, then the cause and generated file a check adds, and the holder a locked file names.</summary>
    private sealed record DiagnosticLine(string File, int? Line, int? Column, string Code, string Message, string? Cause = null, string? Generated = null, string? LockedBy = null);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(DiagnosticLine))]
    private sealed partial class Lines : JsonSerializerContext
    {
    }
}
