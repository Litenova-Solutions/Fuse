using Fuse.Workspace;
using Xunit;

namespace Fuse.Workspace.Tests;

// G8: best-effort extraction of the dotnet command sequence from a CI workflow, and classification of the steps
// that cannot be rehearsed locally. Line-based and dependency-free; the tests cover the shapes seen on the corpus
// (single-line run:, run: | blocks, secret-bearing and package-push steps).
public sealed class CiWorkflowParserTests
{
    [Fact]
    public void Extracts_single_line_dotnet_steps_in_order()
    {
        var result = CiWorkflowParser.Parse("""
            jobs:
              build:
                steps:
                  - uses: actions/setup-dotnet@v4
                  - run: dotnet build ./eShopOnWeb.sln --configuration Release
                  - run: dotnet test ./eShopOnWeb.sln --configuration Release
            """);

        Assert.Equal(2, result.RehearsableCommands.Count);
        Assert.Equal("dotnet build ./eShopOnWeb.sln --configuration Release", result.RehearsableCommands[0]);
        Assert.Equal("dotnet test ./eShopOnWeb.sln --configuration Release", result.RehearsableCommands[1]);
        Assert.Empty(result.NonRehearsableSteps);
    }

    [Fact]
    public void Extracts_dotnet_commands_from_a_block_scalar()
    {
        var result = CiWorkflowParser.Parse("""
            steps:
              - name: Build and test
                run: |
                  dotnet restore
                  dotnet build --no-restore
                  echo done
                  dotnet test --no-build
            """);

        Assert.Equal(3, result.RehearsableCommands.Count);
        Assert.Contains("dotnet restore", result.RehearsableCommands);
        Assert.Contains("dotnet build --no-restore", result.RehearsableCommands);
        Assert.Contains("dotnet test --no-build", result.RehearsableCommands);
        // The non-dotnet "echo done" line is ignored (out of scope), not misclassified.
        Assert.DoesNotContain(result.RehearsableCommands, c => c.Contains("echo"));
    }

    [Fact]
    public void Classifies_a_secret_bearing_push_as_non_rehearsable()
    {
        var result = CiWorkflowParser.Parse("""
            steps:
              - run: dotnet pack src/Lib.csproj --configuration Release --output ./artifacts
              - run: dotnet nuget push ./artifacts/*.nupkg --api-key ${{ secrets.NUGET_TOKEN }}
            """);

        // The pack step is rehearsable; the push step needs a secret and pushes, so it is named non-rehearsable.
        Assert.Contains("dotnet pack src/Lib.csproj --configuration Release --output ./artifacts", result.RehearsableCommands);
        Assert.Single(result.NonRehearsableSteps);
        Assert.Contains("dotnet nuget push", result.NonRehearsableSteps[0]);
    }

    [Fact]
    public void Ignores_non_dotnet_and_empty_workflows()
    {
        var result = CiWorkflowParser.Parse("""
            steps:
              - run: echo hello
              - uses: actions/checkout@v4
            """);

        Assert.Empty(result.RehearsableCommands);
        Assert.Empty(result.NonRehearsableSteps);
        Assert.Empty(CiWorkflowParser.Parse("").RehearsableCommands);
    }

    [Fact]
    public void Recognizes_a_dotnet_test_wrapped_in_a_coverage_tool()
    {
        // Scrutor's shape: a coverage collector wrapping "dotnet test". The dotnet invocation is still detected.
        var result = CiWorkflowParser.Parse("""
            steps:
              - run: dotnet-coverage collect --output cov.xml "dotnet test"
            """);

        // "dotnet-coverage" is not a dotnet invocation (hyphen-joined), but the embedded "dotnet test" is.
        Assert.Single(result.RehearsableCommands);
        Assert.Contains("dotnet test", result.RehearsableCommands[0]);
    }
}
