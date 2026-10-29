using System.Text.Json;
using Fuse.Dotnet;
using Fuse.Operations;

namespace Fuse.Tests.Unit;

public class BuildJsonRenderTests
{
    private static ProcessResult Failed(string output) => new(1, output);

    [Fact]
    public void Json_emits_lockedBy_on_a_locked_file_diagnostic()
    {
        const string output = """App/App.csproj : error MSB3027: Could not copy "Lib/bin/Debug/Lib.dll" to "App/bin/Debug/Lib.dll". Exceeded retry count of 10. Failed. The file is locked by: "NightHall.WebApi (4212)".""";

        var answer = BuildOperation.Render(Failed(output), "/repo", 3, "build", new BuildOptions(int.MaxValue, true, false));

        Assert.Equal(Outcome.ProblemsFound, answer.Outcome);
        var lines = answer.Text.Split('\n');
        Assert.Equal(2, lines.Length);
        using var parsed = JsonDocument.Parse(lines[0]);
        Assert.Equal("NightHall.WebApi (pid 4212)", parsed.RootElement.GetProperty("lockedBy").GetString());
        Assert.Equal("fuse: build failed with 1 error(s) in 3.0 s", lines[^1]);
    }

    [Fact]
    public void Json_omits_lockedBy_when_the_error_names_no_holder()
    {
        const string output = """App/App.csproj : error MSB3021: Could not copy "a.dll" to "b.dll". The process cannot access the file 'b.dll' because it is being used by another process.""";

        var answer = BuildOperation.Render(Failed(output), "/repo", 3, "build", new BuildOptions(int.MaxValue, true, false));

        using var parsed = JsonDocument.Parse(answer.Text.Split('\n')[0]);
        Assert.False(parsed.RootElement.TryGetProperty("lockedBy", out _));
    }

    [Fact]
    public void Json_without_a_parseable_diagnostic_prints_the_output_tail()
    {
        var answer = BuildOperation.Render(Failed("neither an error line\nnor this one is"), "/repo", 3, "build", new BuildOptions(int.MaxValue, true, false));

        Assert.Equal(Outcome.ProblemsFound, answer.Outcome);
        Assert.Equal(
            [
                "neither an error line",
                "nor this one is",
                "fuse: build failed (exit code 1) in 3.0 s",
            ],
            answer.Text.Split('\n'));
    }
}
