using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

public class TestDashDashProcessTests
{
    [Theory]
    [InlineData("--all")]
    [InlineData("--failed")]
    public async Task After_two_dashes_a_fuse_option_goes_to_dotnet_test(string option)
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            var result = await FuseProcess.RunAsync(repo.Path, null, "test", "--", option);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("MSB1001", result.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("cannot be combined", result.Stdout + result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }
}
