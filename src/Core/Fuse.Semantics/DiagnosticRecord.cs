namespace Fuse.Semantics;

/// <summary>
///     The severity of a semantic indexing diagnostic.
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>Informational, for example a clean fallback to syntax mode.</summary>
    Info,

    /// <summary>A recoverable problem, for example one project failing to load while others succeed.</summary>
    Warning,

    /// <summary>A failure that prevented semantic loading entirely.</summary>
    Error,
}

/// <summary>
///     A diagnostic emitted during semantic indexing (workspace load failures, fallback notices, and similar).
/// </summary>
/// <param name="Severity">The severity.</param>
/// <param name="Code">A short stable code (for example <c>msbuild-load-failed</c>).</param>
/// <param name="Message">A human-readable message.</param>
/// <param name="Path">The file or project the diagnostic relates to, when applicable.</param>
public sealed record DiagnosticRecord(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? Path = null);
