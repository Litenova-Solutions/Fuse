using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

public class HookHelpProcessTests
{
    [Fact]
    public async Task Hook_help_prints_the_hooks_usage_to_stderr()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });

        var result = await FuseProcess.RunAsync(repo.Path, null, "hook", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.StartsWith("usage: fuse hook <", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("post-edit", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hook_help_after_an_event_prints_the_hooks_usage_too()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });

        var result = await FuseProcess.RunAsync(repo.Path, null, "hook", "claude", "post-edit", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.StartsWith("usage: fuse hook <", result.Stderr, StringComparison.Ordinal);
    }
}
