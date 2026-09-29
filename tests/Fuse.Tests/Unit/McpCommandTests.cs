using System.Text.Json;
using Fuse.Mcp;
using Fuse.Tests.Fixtures;
using ModelContextProtocol.Protocol;

namespace Fuse.Tests.Unit;

/// <summary>
///     A <c>fuse_check</c> call that names a file by an empty string, a string that is not a path, or something that is not
///     a string is an argument Fuse cannot check, so it is refused as an error with the message that names it, before any
///     engine starts.
/// </summary>
public class McpCommandTests
{
    [Theory]
    [InlineData("""[""]""", "fuse: a file named in the check is empty; name each file by its path")]
    [InlineData("""["Lib/Calc.cs", "  "]""", "fuse: a file named in the check is empty; name each file by its path")]
    [InlineData("""[42]""", "fuse: a file named in the check is empty; name each file by its path")]
    [InlineData("""["Lib/Ca\u0000lc.cs"]""", "fuse: \"Lib/Ca\0lc.cs\" named in the check is not a valid path")]
    public async Task A_check_naming_a_file_that_is_not_a_path_is_an_error(string files, string text)
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Lib.csproj"] = "<Project />" });
        using var document = JsonDocument.Parse(files);
        var request = new CallToolRequestParams { Name = "fuse_check", Arguments = new Dictionary<string, JsonElement> { ["files"] = document.RootElement.Clone() } };

        var result = await McpCommand.CallAsync(request, repo.Path, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Equal(text, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }
}
