namespace Fuse.Emission.Models;

/// <summary>
///     A read-only view of one file in a scoped result's context plan: its role, the reduction tier it was
///     planned at, and its relevance score. This is the public projection of the internal planning model,
///     surfaced on <see cref="FusionResult" /> so an explain surface (the VS Code extension's scope-result and
///     explainer panels) can show why a file was included and at what fidelity, without re-running scoping.
/// </summary>
/// <param name="Path">The normalized repository-relative file path.</param>
/// <param name="Role">The file's role in the result (for example <c>Seed</c>, <c>Dependency</c>, <c>Changed</c>).</param>
/// <param name="Tier">The reduction tier the file was planned at (for example <c>Standard</c>, <c>Skeleton</c>).</param>
/// <param name="Score">The relevance score from scoping, or <c>0</c> when the file was not scored.</param>
public sealed record PlannedFileInfo(string Path, string Role, string Tier, double Score);
