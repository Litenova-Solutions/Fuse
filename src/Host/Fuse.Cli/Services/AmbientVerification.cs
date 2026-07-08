using System.Text;
using Fuse.Cli.Rpc;

namespace Fuse.Cli.Services;

/// <summary>
///     The pure decision and rendering logic behind the S3 ambient-verification commands (<c>fuse check --delta</c>
///     and <c>fuse gate</c>): given a <see cref="CheckDeltaDto" /> from the host's <c>fuse/check</c> RPC, produce
///     the transcript text a PostToolUse hook emits and the pass/fail verdict a Stop hook (<c>fuse gate</c>) blocks
///     on. Kept transport-free so it is unit-tested without a running host or pipe.
/// </summary>
/// <remarks>
///     Baseline discipline (the item's design constraint): "red" means only the diagnostics the session itself
///     introduced, never pre-existing repo diagnostics, so an agent working a dirty repo is not walled in. The
///     hook emits nothing on an empty delta (no transcript spam), and a non-resident delta is treated as empty
///     (the hook stays silent when no resident workspace serves the root).
/// </remarks>
public static class AmbientVerification
{
    /// <summary>
    ///     Renders the PostToolUse hook text for a delta: the introduced diagnostics (what the edit broke) first,
    ///     then the resolved ones. Returns an empty string when nothing changed or no resident workspace served the
    ///     delta, so the hook emits nothing.
    /// </summary>
    /// <param name="delta">The delta from <c>fuse/check</c>.</param>
    /// <returns>The hook text, or an empty string when there is nothing to report.</returns>
    public static string RenderDelta(CheckDeltaDto delta)
    {
        if (!delta.Resident || (delta.Introduced.Count == 0 && delta.Resolved.Count == 0))
            return string.Empty;

        var builder = new StringBuilder();
        builder.AppendLine($"fuse: {delta.Introduced.Count} diagnostic(s) introduced, {delta.Resolved.Count} resolved by your change.");
        foreach (var d in delta.Introduced)
            builder.AppendLine($"  introduced {d.Severity} {d.Id} {Location(d)}: {d.Message}");
        foreach (var d in delta.Resolved)
            builder.AppendLine($"  resolved {d.Severity} {d.Id} {Location(d)}: {d.Message}");
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    ///     Whether the session introduced an error-severity diagnostic, so <c>fuse gate</c> should block a Stop
    ///     that would end the turn red. Only introduced errors count (baseline discipline): a pre-existing error
    ///     the session did not cause, or an introduced warning, does not block.
    /// </summary>
    /// <param name="delta">The delta from <c>fuse/check</c>.</param>
    /// <returns><see langword="true" /> when an introduced error should block the turn; otherwise <see langword="false" />.</returns>
    public static bool IsRed(CheckDeltaDto delta) =>
        delta.Resident && delta.Introduced.Any(d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     Renders the <c>fuse gate</c> summary shown when the gate blocks: the introduced errors the session must
    ///     resolve before ending the turn.
    /// </summary>
    /// <param name="delta">The delta from <c>fuse/check</c>.</param>
    /// <returns>The red summary text.</returns>
    public static string RenderGateBlock(CheckDeltaDto delta)
    {
        var errors = delta.Introduced.Where(d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase)).ToList();
        var builder = new StringBuilder();
        builder.AppendLine($"fuse gate: {errors.Count} error(s) introduced this session remain unresolved. Resolve them or revert before ending the turn:");
        foreach (var d in errors)
            builder.AppendLine($"  {d.Id} {Location(d)}: {d.Message}");
        return builder.ToString().TrimEnd();
    }

    private static string Location(CheckDiagnosticDto d) =>
        d.Path is null ? $"line {d.Line}" : $"{d.Path}:{d.Line}";
}
