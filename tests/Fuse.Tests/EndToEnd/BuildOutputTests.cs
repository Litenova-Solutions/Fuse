using Fuse.Operations;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

/// <summary>
///     A real <c>dotnet build</c> through <see cref="BuildOperation"/>: when a command a target runs exits with a code
///     other than 0, the answer prints what the command printed under the error, which the build's own output leaves out.
/// </summary>
public class BuildOutputTests
{
    [Fact]
    public async Task A_command_that_fails_in_a_target_prints_its_output_under_its_error()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            [".gitignore"] = "bin/\nobj/\n",
            ["Gen/Gen.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <Target Name="Generate" BeforeTargets="CoreCompile">
                    <Exec Command="dotnet fuse-tests-no-such-tool" />
                  </Target>
                </Project>
                """,
            ["Gen/Code.cs"] = "namespace Gen;\n\npublic static class Code\n{\n}\n",
        });

        var answer = await BuildOperation.RunAsync(repo.Root, repo.Full("Gen"), [], TestContext.Current.CancellationToken);

        Assert.Equal(Outcome.ProblemsFound, answer.Outcome);
        var lines = answer.Text.Split('\n');
        var error = Array.FindIndex(lines, l => l.Contains("error MSB3073: The command \"dotnet fuse-tests-no-such-tool\"", StringComparison.Ordinal));
        Assert.True(error >= 0, answer.Text);
        // dotnet says the tool does not exist; the build's own output says only that the command exited with code 1.
        Assert.Contains(lines.Skip(error + 1).TakeWhile(l => l.StartsWith("  ", StringComparison.Ordinal)), l => l.Contains("dotnet-fuse-tests-no-such-tool does not exist", StringComparison.Ordinal));
        Assert.StartsWith("fuse: build failed with 1 error(s) in ", lines[^1], StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(repo.Root.StateDirectory, "build-*.log"));
    }
}
