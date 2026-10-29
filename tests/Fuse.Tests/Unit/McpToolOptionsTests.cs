using System.Text.Json;
using Fuse.Mcp;
using Fuse.Tests.Fixtures;
using ModelContextProtocol.Protocol;

namespace Fuse.Tests.Unit;

/// <summary>The options of <c>fuse_check</c> and <c>fuse_test</c>: <c>failed</c> reruns the last failures and is refused with <c>all</c>, and <c>max_errors</c> caps the errors shown.</summary>
public class McpToolOptionsTests
{
    private static CallToolRequestParams Request(string name, string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        return new CallToolRequestParams
        {
            Name = name,
            Arguments = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()),
        };
    }

    private static string TextOf(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    [Fact]
    public async Task Test_with_all_and_failed_is_refused()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Lib.csproj"] = "<Project />" });

        var result = await McpCommand.CallAsync(Request("fuse_test", """{"all":true,"failed":true}"""), repo.Path, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Equal("fuse: failed reruns the tests that failed last time and cannot be combined with all; set only one of them", TextOf(result));
    }

    [Fact]
    public async Task Test_with_failed_and_no_recorded_run_names_fuse_test()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Lib.csproj"] = "<Project />" });

        var result = await McpCommand.CallAsync(Request("fuse_test", """{"failed":true}"""), repo.Path, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Equal("fuse: no test run is recorded yet; run fuse test first", TextOf(result));
    }

    [Theory]
    [InlineData("""{"max_errors":0}""")]
    [InlineData("""{"max_errors":100001}""")]
    [InlineData("""{"max_errors":-3}""")]
    [InlineData("""{"max_errors":"many"}""")]
    [InlineData("""{"max_errors":true}""")]
    [InlineData("""{"max_errors":2.5}""")]
    public async Task Check_with_a_max_errors_that_is_no_count_is_refused(string arguments)
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Lib.csproj"] = "<Project />" });

        var result = await McpCommand.CallAsync(Request("fuse_check", arguments), repo.Path, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Equal("fuse: max_errors takes a count of 1 to 100000", TextOf(result));
    }

    [Fact]
    public async Task Check_with_a_valid_max_errors_still_reports_a_file_that_is_not_a_path()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Lib.csproj"] = "<Project />" });

        var result = await McpCommand.CallAsync(Request("fuse_check", """{"max_errors":5,"files":[""]}"""), repo.Path, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Equal("fuse: a file named in the check is empty; name each file by its path", TextOf(result));
    }
}
