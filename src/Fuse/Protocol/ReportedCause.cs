namespace Fuse.Protocol;

/// <summary>The declaration change that put an error's file in the check, which the client prints under the error.</summary>
/// <param name="Kind">Whether the working tree still has the declaration.</param>
/// <param name="Declaration">
///     The declaration's header on one line: as the working tree has it when it changed, and as HEAD had it when it was
///     removed.
/// </param>
internal sealed record ReportedCause(CauseKind Kind, string Declaration);
