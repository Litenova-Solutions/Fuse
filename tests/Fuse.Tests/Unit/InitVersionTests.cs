using Fuse.Harnesses;
using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>
///     <c>fuse init</c> stamps its closing line with the version that wrote the settings, and its instruction section
///     names the container pre-flight and the C# scope.
/// </summary>
public class InitVersionTests
{
    private static string Init(FixtureRepo repo, bool instructions = false)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, InitCommand.Run(repo.Root.Path, output, error, instructions));
        return output.ToString();
    }

    [Fact]
    public void The_hooks_closing_line_names_the_version_that_registered_them()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["App/App.csproj"] = "<Project />" });

        Assert.Contains($"fuse: hooks registered by Fuse {EngineVersion.Product}; after each edit", Init(repo), StringComparison.Ordinal);
    }

    [Fact]
    public void The_mcp_only_closing_line_names_the_version_too()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["App/App.csproj"] = "<Project />",
            [".vscode/settings.json"] = "{}",
        });

        Assert.Contains($"fuse: MCP server registered in .vscode/mcp.json by Fuse {EngineVersion.Product};", Init(repo), StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_run_still_names_unchanged_files_and_the_version()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["App/App.csproj"] = "<Project />" });

        Init(repo);
        var second = Init(repo);

        Assert.StartsWith("unchanged .claude/settings.json", second, StringComparison.Ordinal);
        Assert.Contains($"by Fuse {EngineVersion.Product}", second, StringComparison.Ordinal);
    }

    [Fact]
    public void The_instructions_section_names_the_container_preflight_and_the_csharp_scope()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["App/App.csproj"] = "<Project />" });

        Init(repo, instructions: true);
        var text = File.ReadAllText(repo.Full("AGENTS.md"));

        Assert.Contains("- `fuse test` skips test projects that need a container runtime when none is running, and says so; start Docker or Podman to run them.", text, StringComparison.Ordinal);
        Assert.Contains("- Fuse checks C# only; `fuse check` names changed files in other languages, which your repository's own gates cover.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_later_instructions_run_replaces_the_section_without_duplicating_it()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["App/App.csproj"] = "<Project />" });

        Init(repo, instructions: true);
        var second = Init(repo, instructions: true);
        var text = File.ReadAllText(repo.Full("AGENTS.md"));

        Assert.Contains("unchanged AGENTS.md", second, StringComparison.Ordinal);
        Assert.Single(text.Split(InstructionsFile.Begin)[1..]);
    }

    [Fact]
    public void Remove_strips_the_whole_section()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["App/App.csproj"] = "<Project />" });

        Init(repo, instructions: true);

        Assert.NotNull(InstructionsFile.Remove(repo.Root, "AGENTS.md"));
        Assert.False(File.Exists(repo.Full("AGENTS.md")));
    }
}
