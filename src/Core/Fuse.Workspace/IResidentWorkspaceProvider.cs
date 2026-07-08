using Fuse.Indexing;

namespace Fuse.Workspace;

/// <summary>
///     A one-line description of the resident workspace serving a repository root: how many projects it holds
///     live and as of when (S1). This is the light-weight status a read path reports in its availability header
///     ("resident, current as of ...") without exposing the heavy compilation state.
/// </summary>
/// <param name="ProjectCount">The number of projects held live in the resident workspace.</param>
/// <param name="AsOf">A human-readable stamp of when the resident state was last current (set by the host).</param>
public sealed record ResidentStatus(int ProjectCount, string AsOf);

/// <summary>
///     The seam by which a read path asks whether a live resident workspace is serving a repository root, so an
///     answer can be labelled resident-grade rather than store-grade (S1, Decision D8: resident truth first, the
///     store second). The default implementation reports no resident workspace, so a process that has not wired a
///     resident engine behaves exactly as the store-backed path always has.
/// </summary>
public interface IResidentWorkspaceProvider
{
    /// <summary>
    ///     Describes the resident workspace serving a root, if one is live.
    /// </summary>
    /// <param name="root">The absolute workspace root.</param>
    /// <returns>The resident status, or null when no resident workspace serves that root (store-backed).</returns>
    ResidentStatus? DescribeResident(string root);

    /// <summary>
    ///     Speculatively typechecks a proposed single-file edit against the resident workspace serving a root, if
    ///     one is live (S1: the oracle-grade check served from the live compilation, no build, no disk write).
    /// </summary>
    /// <param name="root">The absolute workspace root.</param>
    /// <param name="relativeFilePath">The repo-relative path of the file being changed.</param>
    /// <param name="newContent">The proposed full new content of that file.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>
    ///     The changed document's diagnostics, or null when no resident workspace serves the root or the file is
    ///     not in it (the caller then falls back to the build-capture worker or build-grade path).
    /// </returns>
    IReadOnlyList<CheckDiagnostic>? TryCheckOverlay(
        string root, string relativeFilePath, string newContent, CancellationToken cancellationToken);

    /// <summary>
    ///     Returns the whole-state diagnostics of the resident workspace serving a root, if one is live: the
    ///     current diagnostics across every held compilation (S1's <c>GetDiagnostics</c>). This is what
    ///     <c>fuse_check</c> delta mode (S2) diffs a persisted session baseline against, so a delta is computed
    ///     without running a build. Null when no resident workspace serves the root (delta mode then abstains,
    ///     since it must not run a build).
    /// </summary>
    /// <param name="root">The absolute workspace root.</param>
    /// <returns>The current whole-state diagnostics, or null when no resident workspace serves the root.</returns>
    IReadOnlyList<CheckDiagnostic>? TryGetCurrentDiagnostics(string root) => null;
}

/// <summary>
///     The default <see cref="IResidentWorkspaceProvider" />: reports no resident workspace for any root, so a
///     process without a wired resident engine answers store-backed, exactly as before S1. The host that holds a
///     resident workspace replaces this with a provider backed by its live state.
/// </summary>
public sealed class NullResidentWorkspaceProvider : IResidentWorkspaceProvider
{
    /// <summary>The shared instance.</summary>
    public static readonly NullResidentWorkspaceProvider Instance = new();

    private NullResidentWorkspaceProvider()
    {
    }

    /// <inheritdoc />
    public ResidentStatus? DescribeResident(string root) => null;

    /// <inheritdoc />
    public IReadOnlyList<CheckDiagnostic>? TryCheckOverlay(
        string root, string relativeFilePath, string newContent, CancellationToken cancellationToken) => null;
}
