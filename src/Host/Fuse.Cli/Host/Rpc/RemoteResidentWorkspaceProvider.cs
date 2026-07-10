using Fuse.Indexing;
using Fuse.Workspace;

namespace Fuse.Cli.Rpc;

/// <summary>
///     An <see cref="IResidentWorkspaceProvider" /> that delegates the resident-grade check to a shared daemon
///     over the pipe (G5), instead of holding its own resident workspace. A non-owner process (for example an
///     <c>mcp serve</c> that lost the resident-owner arbitration to a running <c>fuse host</c>) installs this so
///     one daemon-held compilation serves every client, which is the RSS reduction G5 delivers. When no daemon
///     answers, every method returns the "no resident" default, so the caller falls back to its own path cleanly.
/// </summary>
public sealed class RemoteResidentWorkspaceProvider : IResidentWorkspaceProvider
{
    private readonly Func<string, string, string, bool, CancellationToken, Task<CheckOverlayResultDto?>> _checkOverlay;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RemoteResidentWorkspaceProvider" /> class.
    /// </summary>
    /// <param name="checkOverlay">
    ///     The overlay-check call to the daemon (root, file, content, includeAnalyzers, token). Injected so the
    ///     delegation is testable; the production default calls <see cref="FuseHostClient.TryCheckOverlayAsync" />.
    /// </param>
    public RemoteResidentWorkspaceProvider(
        Func<string, string, string, bool, CancellationToken, Task<CheckOverlayResultDto?>>? checkOverlay = null) =>
        _checkOverlay = checkOverlay ?? ((root, file, content, analyzers, ct) =>
            FuseHostClient.TryCheckOverlayAsync(root, file, content, analyzers, TimeSpan.FromSeconds(2), ct));

    /// <inheritdoc />
    /// <remarks>The proxy does not describe the remote workspace synchronously; the availability header treats a
    /// null here as store-backed. The resident-grade value is delivered through <see cref="TryCheckOverlayAsync" />.</remarks>
    public ResidentStatus? DescribeResident(string root) => null;

    /// <inheritdoc />
    /// <remarks>The synchronous overlay check is not proxied (it would block on a pipe round trip); the async
    /// path is the one <c>fuse_check</c> uses.</remarks>
    public IReadOnlyList<CheckDiagnostic>? TryCheckOverlay(
        string root, string relativeFilePath, string newContent, CancellationToken cancellationToken) => null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CheckDiagnostic>?> TryCheckOverlayAsync(
        string root, string relativeFilePath, string newContent, bool includeAnalyzers, CancellationToken cancellationToken)
    {
        var result = await _checkOverlay(root, relativeFilePath, newContent, includeAnalyzers, cancellationToken);
        if (result is null || !result.HasResident)
            return null; // No daemon, or the daemon has no resident workspace: the caller falls back.
        return result.Diagnostics
            .Select(d => new CheckDiagnostic(d.Id, d.Severity, d.Message, d.Path, d.Line))
            .ToList();
    }
}
