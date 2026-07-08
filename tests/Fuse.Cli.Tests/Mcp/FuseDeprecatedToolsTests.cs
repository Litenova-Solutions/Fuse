using Fuse.Cli.Mcp;

namespace Fuse.Cli.Tests.Mcp;

// The retired V2 tool names still resolve and return an actionable message naming their V3 replacement,
// so a client that cached the old surface across an upgrade is not met with a bare Unknown tool error.
public sealed class FuseDeprecatedToolsTests
{
    [Fact]
    public void ShimsNameTheirV3Replacement()
    {
        Assert.Contains("fuse_workspace", FuseDeprecatedTools.FuseToc());
        Assert.Contains("fuse_review", FuseDeprecatedTools.FuseChanges());
        Assert.Contains("fuse_find", FuseDeprecatedTools.FuseSearch());
        Assert.Contains("fuse_context", FuseDeprecatedTools.FuseFocus());
        Assert.Contains("fuse_find", FuseDeprecatedTools.FuseAsk());
        // The U1 folds resolve to their union tool.
        Assert.Contains("fuse_workspace", FuseDeprecatedTools.FuseIndex());
        Assert.Contains("fuse_workspace", FuseDeprecatedTools.FuseMap());
        Assert.Contains("fuse_find", FuseDeprecatedTools.FuseLocalize());
        Assert.Contains("fuse_find", FuseDeprecatedTools.FuseResolve());
        Assert.Contains("fuse_find", FuseDeprecatedTools.FuseNeighbors());
        Assert.Contains("fuse_find", FuseDeprecatedTools.FuseSignatures());
        // The changeset shim names the replacement flow (check + refactor + workspace apply).
        var changeset = FuseDeprecatedTools.FuseChangeset();
        Assert.Contains("fuse_check", changeset);
        Assert.Contains("fuse_workspace", changeset);
    }

    [Fact]
    public void ShimsNameTheRetiredToolAndPointToReconnect()
    {
        var message = FuseDeprecatedTools.FuseToc();

        Assert.Contains("fuse_toc", message);
        Assert.Contains("reconnect", message, StringComparison.OrdinalIgnoreCase);
    }
}
