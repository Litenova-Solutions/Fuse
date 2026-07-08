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
}
