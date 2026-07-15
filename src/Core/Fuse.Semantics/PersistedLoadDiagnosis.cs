using System.Text.Json.Serialization;

namespace Fuse.Semantics;

/// <summary>
///     The per-project semantic-load diagnosis stamped into <c>index_meta</c> at index time (R43), so
///     <c>fuse_workspace action=doctor</c> reports the achieved tier and per-project reasons from the warm index
///     without re-running the full MSBuild/Roslyn load. It carries exactly the fields doctor prints; the raw load
///     diagnostics (which doctor does not print) are omitted to keep the stamp small.
/// </summary>
/// <param name="Tier">The achieved load tier (for example <c>oracle-grade (all projects loaded clean)</c>).</param>
/// <param name="ProjectsLoaded">The number of projects that produced a compilation.</param>
/// <param name="ProjectsTotal">The number of projects considered.</param>
/// <param name="Projects">The per-project load outcomes.</param>
/// <param name="SelectedSolution">The selected solution (or a project-set description), or null for syntax-only.</param>
/// <param name="SelectionNote">A warning when discovery was ambiguous or selected a fixture solution, else null.</param>
public sealed record PersistedLoadDiagnosis(
    string Tier,
    int ProjectsLoaded,
    int ProjectsTotal,
    IReadOnlyList<PersistedProjectReport> Projects,
    string? SelectedSolution,
    string? SelectionNote);

/// <summary>One project's load outcome in a <see cref="PersistedLoadDiagnosis" />.</summary>
/// <param name="Name">The project name.</param>
/// <param name="FilePath">The project file path.</param>
/// <param name="Loaded">Whether the project produced a compilation.</param>
/// <param name="Reason">The concrete outcome (loaded, loaded-with-errors, or why it did not load).</param>
public sealed record PersistedProjectReport(string Name, string FilePath, bool Loaded, string Reason);

/// <summary>
///     The source-generated JSON context for the persisted load diagnosis, so serialization uses no reflection
///     (the repo's JSON invariant).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PersistedLoadDiagnosis))]
public sealed partial class PersistedLoadDiagnosisJsonContext : JsonSerializerContext;
