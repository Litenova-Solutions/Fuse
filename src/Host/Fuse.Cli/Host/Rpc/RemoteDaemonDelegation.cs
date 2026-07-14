using Fuse.Cli.Mcp;
using Fuse.Workspace;

namespace Fuse.Cli.Rpc;

/// <summary>
///     Wires MCP serve to delegate both resident-grade checks and index writes to a shared <c>fuse host</c> daemon
///     (G5, R19). Restores the local providers on dispose so tests and daemon-less paths stay isolated.
/// </summary>
public sealed class RemoteDaemonDelegation : IDisposable
{
    private readonly IResidentWorkspaceProvider _previousResident;
    private readonly IIndexAccessProvider _previousIndex;

    /// <summary>Installs remote resident and index providers on <see cref="FuseTools" />.</summary>
    public RemoteDaemonDelegation()
    {
        _previousResident = FuseTools.ResidentWorkspaces;
        _previousIndex = FuseTools.IndexAccess;
        FuseTools.ResidentWorkspaces = new RemoteResidentWorkspaceProvider();
        FuseTools.IndexAccess = new RemoteIndexAccessProvider();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        FuseTools.ResidentWorkspaces = _previousResident;
        FuseTools.IndexAccess = _previousIndex;
    }
}
