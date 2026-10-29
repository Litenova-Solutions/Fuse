using System.Text.Json;
using System.Text.Json.Serialization;
using Fuse.Dotnet;
using Fuse.Protocol;

namespace Fuse.Operations;

/// <summary>One diagnostic as one JSON line, for <c>--json</c>: build and check share the shape.</summary>
internal static partial class DiagnosticJson
{
    /// <summary>One build diagnostic as one JSON object.</summary>
    public static string BuildLine(BuildDiagnostic diagnostic) =>
        JsonSerializer.Serialize(
            new DiagnosticLine(diagnostic.File, diagnostic.Line, diagnostic.Column, diagnostic.Code, diagnostic.Message, LockedBy: LockLine.Holder(diagnostic)),
            Lines.Default.DiagnosticLine);

    /// <summary>One introduced error as one JSON object.</summary>
    public static string CheckLine(ReportedError reported) =>
        JsonSerializer.Serialize(
            new DiagnosticLine(reported.Error.Path, reported.Error.Line, reported.Error.Column, reported.Error.Id, reported.Error.Message, CauseOf(reported.Cause), reported.GeneratedFile),
            Lines.Default.DiagnosticLine);

    /// <summary>The cause as the text answer prints it under the error, or null for none.</summary>
    private static string? CauseOf(ReportedCause? cause) =>
        cause is null ? null : $"{(cause.Kind == CauseKind.Removed ? "removed" : "changed")}: {cause.Declaration}";

    /// <summary>One diagnostic line: the build fields, then what only a check or a locked file adds.</summary>
    private sealed record DiagnosticLine(string File, int? Line, int? Column, string Code, string Message, string? Cause = null, string? Generated = null, string? LockedBy = null);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(DiagnosticLine))]
    private sealed partial class Lines : JsonSerializerContext
    {
    }
}
