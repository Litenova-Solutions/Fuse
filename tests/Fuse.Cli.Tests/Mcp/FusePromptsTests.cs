using Fuse.Cli.Mcp;

namespace Fuse.Cli.Tests.Mcp;

public sealed class FusePromptsTests
{
    [Fact]
    public void FixBuildError_DoesNotClaimReviewProvesRegressionSafety()
    {
        var prompt = FusePrompts.FixBuildError("CS1061");

        Assert.Contains("run the repository's required gates", prompt);
        Assert.Contains("fuse_review does not prove compiler or test success", prompt);
        Assert.DoesNotContain("nothing else regressed", prompt);
    }

    [Fact]
    public void ImplementFeature_StatesSingleFileCheckBoundary()
    {
        var prompt = FusePrompts.ImplementFeature("Add order cancellation");

        Assert.Contains("cannot verify a coordinated multi-file overlay", prompt);
        Assert.Contains("run the repository gates", prompt);
    }

    [Fact]
    public void RenameSymbol_UsesNormalToolsForMultiFileDiff()
    {
        var prompt = FusePrompts.RenameSymbol("App.OrderService");

        Assert.Contains("apply the staged multi-file diff with normal editing tools", prompt);
        Assert.Contains("does not apply a multi-file patch", prompt);
        Assert.DoesNotContain("fuse_workspace action=apply write=true", prompt);
    }

    [Fact]
    public void AddEndpoint_DistinguishesStandaloneAndCoordinatedEdits()
    {
        var prompt = FusePrompts.AddEndpoint("/api/orders");

        Assert.Contains("standalone single-file edit", prompt);
        Assert.Contains("For coordinated edits", prompt);
        Assert.Contains("run the repository's required gates", prompt);
    }
}
