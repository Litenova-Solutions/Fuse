using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fuse.Semantics.Remediation;

/// <summary>
///     One project's line in a machine-readable <c>fuse up</c> report (C1): the JSON-friendly projection of a
///     <see cref="RemediationPlanItem" />, flattening the matched signature so a consumer (workspace status, the
///     up-report benchmark harness) reads it without the plan's computed properties.
/// </summary>
/// <param name="Project">The project name.</param>
/// <param name="Loaded">Whether the project loaded to a compilation (oracle-grade for that project).</param>
/// <param name="Reason">The concrete load reason reported by the loader (empty when loaded).</param>
/// <param name="RemedyId">The matched signature id (for example <c>NU1507</c>), or null when loaded or unrecognized.</param>
/// <param name="RemedyTitle">The matched signature's one-line title, or null.</param>
/// <param name="Remedy">The remedy key the engine dispatches on (for example <c>overlay-nuget-source-mapping</c>, <c>install-sdk</c>, <c>classify-only</c>), or null.</param>
/// <param name="RequiresConsent">Whether the remedy changes the machine and so needs <c>--allow-install</c>.</param>
public sealed record UpProjectReport(
    string Project,
    bool Loaded,
    string Reason,
    string? RemedyId,
    string? RemedyTitle,
    string? Remedy,
    bool RequiresConsent);

/// <summary>
///     The machine-readable snapshot of one workspace's remediation plan (C1): the achieved tier, the
///     workable-subset line, and the per-project classification. This is the same shape workspace status reads
///     and the up-report benchmark harness consolidates, so the report an agent sees at minute zero and the gate
///     artifact are one definition.
/// </summary>
/// <param name="Tier">The achieved load tier (semantic, partial, or syntax).</param>
/// <param name="ProjectsLoaded">The number of projects that loaded to a compilation.</param>
/// <param name="ProjectsTotal">The total number of projects opened.</param>
/// <param name="WorkableSubsetLine">The one-line workable-subset summary.</param>
/// <param name="Remediable">The count of downgraded projects with an applicable (not classify-only) remedy.</param>
/// <param name="Unfixable">The count of downgraded projects whose failure is repository code or unrecognized.</param>
/// <param name="Projects">The per-project classification lines.</param>
public sealed record UpRepoReport(
    string Tier,
    int ProjectsLoaded,
    int ProjectsTotal,
    string WorkableSubsetLine,
    int Remediable,
    int Unfixable,
    IReadOnlyList<UpProjectReport> Projects)
{
    /// <summary>
    ///     Projects a <see cref="RemediationPlan" /> into the machine-readable report shape.
    /// </summary>
    /// <param name="plan">The plan to project.</param>
    /// <returns>The JSON-friendly snapshot.</returns>
    public static UpRepoReport From(RemediationPlan plan) =>
        new(
            plan.Tier,
            plan.ProjectsLoaded,
            plan.ProjectsTotal,
            plan.WorkableSubsetLine,
            plan.Remediable.Count,
            plan.Unfixable.Count,
            plan.Items.Select(i => new UpProjectReport(
                i.Project,
                i.Loaded,
                i.Reason,
                i.Signature?.Id,
                i.Signature?.Title,
                i.Signature?.Remedy,
                i.Signature?.RequiresConsent ?? false)).ToList());
}

/// <summary>
///     A tier-1 build-probe result in the machine-readable report (C1): whether a real <c>dotnet build</c> reached
///     tier-1 (build-capture oracle grade), and the classified blocker when it did not. This is the signal the
///     design-time load tier cannot give, because a restore-only failure (NU1507) or an SDK/workload gap fails the
///     build while the design-time load still resolves a compilation.
/// </summary>
/// <param name="Attempted">Whether the probe ran a build (false when no build target was found or the toolchain was absent).</param>
/// <param name="Succeeded">Whether the build reached tier-1 (exited zero).</param>
/// <param name="TimedOut">Whether the build exceeded the timeout.</param>
/// <param name="BlockerId">The classified blocker signature id (for example <c>NU1507</c>), or null.</param>
/// <param name="BlockerTitle">The blocker's one-line title, or null.</param>
/// <param name="BlockerRemedy">The remedy key for the blocker, or null.</param>
/// <param name="RequiresConsent">Whether the blocker's remedy needs <c>--allow-install</c>.</param>
public sealed record UpBuildProbe(
    bool Attempted,
    bool Succeeded,
    bool TimedOut,
    string? BlockerId,
    string? BlockerTitle,
    string? BlockerRemedy,
    bool RequiresConsent)
{
    /// <summary>Projects a <see cref="BuildProbeResult" /> into the machine-readable report shape.</summary>
    /// <param name="result">The probe result.</param>
    /// <returns>The JSON-friendly snapshot.</returns>
    public static UpBuildProbe From(BuildProbeResult result) =>
        new(
            result.Attempted,
            result.Succeeded,
            result.TimedOut,
            result.Blocker?.Id,
            result.Blocker?.Title,
            result.Blocker?.Remedy,
            result.Blocker?.RequiresConsent ?? false);
}

/// <summary>
///     The result of running <c>fuse up</c> on one workspace (C1): the design-time load plan before any remedy, the
///     plan after the install-free remedies were applied (null when nothing was applied), and the optional tier-1
///     build-probe results (the true oracle-reachability signal). A consumer reads <see cref="After" /> and
///     <see cref="BuildProbeAfter" /> when present, else the before values.
/// </summary>
/// <param name="Root">The workspace root the report is for.</param>
/// <param name="Applied">Whether an install-free remedy was applied and the load re-attempted.</param>
/// <param name="Before">The design-time load plan before any remedy.</param>
/// <param name="After">The design-time load plan after the install-free remedies, or null when none applied.</param>
/// <param name="BuildProbeBefore">The tier-1 build probe before any remedy, or null when the probe did not run.</param>
/// <param name="BuildProbeAfter">The tier-1 build probe after the install-free remedies, or null.</param>
public sealed record UpResult(
    string Root,
    bool Applied,
    UpRepoReport Before,
    UpRepoReport? After,
    UpBuildProbe? BuildProbeBefore = null,
    UpBuildProbe? BuildProbeAfter = null);

/// <summary>
///     Source-generated JSON context for the <c>fuse up</c> machine-readable report, per the project invariant
///     that JSON uses a source-generated <see cref="JsonSerializerContext" /> rather than reflection.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UpResult))]
[JsonSerializable(typeof(UpRepoReport))]
public sealed partial class UpReportJsonContext : JsonSerializerContext;

/// <summary>
///     Serializes a <see cref="UpResult" /> to the machine-readable JSON <c>fuse up --json</c> emits.
/// </summary>
public static class UpReportJson
{
    /// <summary>
    ///     Serializes an up result to indented JSON.
    /// </summary>
    /// <param name="result">The result to serialize.</param>
    /// <returns>The indented JSON document.</returns>
    public static string Serialize(UpResult result) =>
        JsonSerializer.Serialize(result, UpReportJsonContext.Default.UpResult);
}
