using Fuse.Cli.Rpc;
using Xunit;

namespace Fuse.Cli.Tests.Host;

// G5: the proxy provider maps the daemon's overlay result back to the resident-workspace contract. When the
// daemon answers with resident diagnostics they are returned; when no daemon answers, or the daemon has no
// resident workspace, the proxy returns null so the caller falls back to its own path. Injected fetch, no daemon.
public sealed class RemoteResidentWorkspaceProviderTests
{
    [Fact]
    public async Task Maps_daemon_diagnostics_to_the_resident_contract()
    {
        var provider = new RemoteResidentWorkspaceProvider((_, _, _, _, _) => Task.FromResult<CheckOverlayResultDto?>(
            new CheckOverlayResultDto(true, [new CheckDiagnosticDto("CS0246", "Error", "type not found", "A.cs", 4)])));

        var diagnostics = await provider.TryCheckOverlayAsync("/repo", "A.cs", "content", includeAnalyzers: true, CancellationToken.None);

        var diagnostic = Assert.Single(diagnostics!);
        Assert.Equal("CS0246", diagnostic.Id);
        Assert.Equal(4, diagnostic.Line);
    }

    [Fact]
    public async Task Returns_null_when_no_daemon_answers()
    {
        var provider = new RemoteResidentWorkspaceProvider((_, _, _, _, _) => Task.FromResult<CheckOverlayResultDto?>(null));
        Assert.Null(await provider.TryCheckOverlayAsync("/repo", "A.cs", "content", true, CancellationToken.None));
    }

    [Fact]
    public async Task Returns_null_when_the_daemon_has_no_resident_workspace()
    {
        var provider = new RemoteResidentWorkspaceProvider((_, _, _, _, _) => Task.FromResult<CheckOverlayResultDto?>(
            new CheckOverlayResultDto(false, [])));
        Assert.Null(await provider.TryCheckOverlayAsync("/repo", "A.cs", "content", true, CancellationToken.None));
    }
}
