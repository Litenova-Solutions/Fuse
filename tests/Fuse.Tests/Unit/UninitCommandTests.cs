using System.Text.Json.Nodes;
using Fuse.Harnesses;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>
///     <c>fuse uninit</c> takes out everything <c>fuse init</c> and <c>fuse init --instructions</c> wrote, for every harness,
///     and keeps everything else: a file Fuse created for itself is deleted, and a shared file is left as it was before.
/// </summary>
public class UninitCommandTests
{
    private const string Project = "<Project />";

    [Fact]
    public async Task Uninit_after_init_leaves_the_files_of_every_harness_as_they_were()
    {
        // A marker per harness, so init registers all of them, and VS Code for the MCP server.
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["App/App.csproj"] = Project,
            ["CLAUDE.md"] = "# Rules\n",
            ["GEMINI.md"] = "# Gemini\n",
            [".cursor/rules.md"] = "x",
            [".codex/config.toml"] = "x",
            [".opencode/opencode.json"] = "{}",
            [".muse/settings.json"] = "{}",
            [".github/copilot-instructions.md"] = "x",
            [".vscode/settings.json"] = "{}",
        });
        var before = Snapshot(repo);

        Assert.Equal(0, InitCommand.Run(repo.Root.Path, TextWriter.Null, TextWriter.Null, instructions: true));
        Assert.NotEqual(before, Snapshot(repo));
        var (exit, output) = await UninitAsync(repo);

        Assert.Equal(0, exit);
        Assert.Equal(before, Snapshot(repo));
        Assert.Contains("deleted .muse/plugins/fuse", output);
        Assert.Contains("muse plugins remove fuse", output);
        Assert.Contains("removed Fuse from this repository", output);
    }

    [Fact]
    public async Task Uninit_keeps_the_entries_that_are_not_Fuses()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["App/App.csproj"] = Project,
            [".claude/settings.json"] = """
                {
                  "model": "opus",
                  "hooks": { "PostToolUse": [ { "matcher": "Write", "hooks": [ { "type": "command", "command": "prettier --write" } ] } ] },
                  "permissions": { "allow": [ "Bash(dotnet build:*)" ] }
                }
                """,
            [".vscode/mcp.json"] = """{ "servers": { "other": { "type": "stdio", "command": "other" } } }""",
            [".gitignore"] = "bin/\n",
            ["AGENTS.md"] = "# Project\n\nWrite tests.\n",
        });

        Assert.Equal(0, InitCommand.Run(repo.Root.Path, TextWriter.Null, TextWriter.Null, instructions: true));
        // init mirrored the dotnet build permission onto fuse build, which uninit takes out again.
        Assert.Contains("Bash(fuse build:*)", repo.Read(".claude/settings.json"));
        await UninitAsync(repo);

        var settings = JsonNode.Parse(repo.Read(".claude/settings.json"))!;
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{ "model": "opus", "hooks": { "PostToolUse": [ { "matcher": "Write", "hooks": [ { "type": "command", "command": "prettier --write" } ] } ] }, "permissions": { "allow": [ "Bash(dotnet build:*)" ] } }"""),
            settings), settings.ToJsonString());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "servers": { "other": { "type": "stdio", "command": "other" } } }"""), JsonNode.Parse(repo.Read(".vscode/mcp.json"))));
        Assert.Equal("bin/\n", repo.Read(".gitignore"));
        Assert.Equal("# Project\n\nWrite tests.\n", repo.Read("AGENTS.md"));
    }

    [Fact]
    public async Task Uninit_in_a_repository_without_Fuse_changes_nothing_and_says_so()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["App/App.csproj"] = Project, [".gitignore"] = ".fuse\n" });
        var before = Snapshot(repo);

        var (exit, output) = await UninitAsync(repo);

        Assert.Equal(0, exit);
        Assert.Equal(before, Snapshot(repo));
        Assert.Contains("this repository has nothing fuse init wrote", output);
    }

    [Fact]
    public async Task A_second_uninit_has_nothing_left_to_remove()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["App/App.csproj"] = Project });
        Assert.Equal(0, InitCommand.Run(repo.Root.Path, TextWriter.Null, TextWriter.Null, instructions: true));

        await UninitAsync(repo);
        var (_, output) = await UninitAsync(repo);

        Assert.Contains("this repository has nothing fuse init wrote", output);
        Assert.DoesNotContain("deleted ", output.Replace("deleted the engine's state", "", StringComparison.Ordinal));
    }

    private static async Task<(int Exit, string Output)> UninitAsync(FixtureRepo repo)
    {
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        var exit = await UninitCommand.RunAsync(repo.Root.Path, output, error, TestContext.Current.CancellationToken);
        return (exit, output + error.ToString());
    }

    /// <summary>Every file of the working tree outside <c>.git</c>, with its content, in path order.</summary>
    private static string Snapshot(FixtureRepo repo) =>
        string.Join("\n", Directory.EnumerateFiles(repo.Root.Path, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(repo.Root.Path, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith(".git/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(f => $"{f}: {repo.Read(f)}"));
}
