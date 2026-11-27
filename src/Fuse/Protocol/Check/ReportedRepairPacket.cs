namespace Fuse.Protocol.Check;

/// <summary>
///     Deterministic Roslyn repair context attached to a <see cref="ReportedError"/> when requested with
///     <c>--brief</c>, giving agents the nearby source lines, candidate members or overloads, missing usings, and
///     interface stubs without extra file reads.
/// </summary>
/// <param name="SourceWindow">A compact 5-line source window around the error span with line numbers, or null.</param>
/// <param name="Candidates">Nearest Levenshtein-matched members or candidate overload signatures.</param>
/// <param name="SuggestedUsings">Missing <c>using</c> directives declaring an unresolved type.</param>
/// <param name="InterfaceStub">Ready-to-paste signature stub for an unimplemented interface member, or null.</param>
internal sealed record ReportedRepairPacket(
    string? SourceWindow = null,
    string[]? Candidates = null,
    string[]? SuggestedUsings = null,
    string? InterfaceStub = null)
{
    /// <summary>Nearest Levenshtein-matched members or candidate overload signatures. Empty when none apply.</summary>
    public string[] Candidates { get; init; } = Candidates ?? [];

    /// <summary>Missing <c>using</c> directives declaring an unresolved type. Empty when none apply.</summary>
    public string[] SuggestedUsings { get; init; } = SuggestedUsings ?? [];
}
