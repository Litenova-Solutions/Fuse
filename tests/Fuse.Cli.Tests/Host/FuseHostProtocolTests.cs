using Fuse.Cli.Rpc;
using Xunit;

namespace Fuse.Cli.Tests.Host;

// R19: a stale client whose protocol version does not match the daemon treats it as no daemon (G5 handshake).
public sealed class FuseHostProtocolTests
{
    [Fact]
    public void Protocol_version_is_bumped_for_compiler_owner_rpcs()
    {
        Assert.Equal(10, FuseHostService.ProtocolVersion);
    }
}
