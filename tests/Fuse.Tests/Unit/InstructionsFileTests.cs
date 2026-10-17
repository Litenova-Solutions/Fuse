using Fuse.Harnesses;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>
///     <c>fuse init --instructions</c> puts Fuse's section in the instruction files agents read: it creates
///     <c>AGENTS.md</c>, appends to a file without the section, replaces only the section on a later run, and keeps the
///     rest of the file and its line endings.
/// </summary>
public class InstructionsFileTests
{
    private static int Init(FixtureRepo repo, out string output)
    {
        using var writer = new StringWriter();
        using var error = new StringWriter();
        var code = InitCommand.Run(repo.Root.Path, writer, error, instructions: true);
        output = writer.ToString();
        return code;
    }

    [Fact]
    public void A_missing_AGENTS_md_is_created_with_the_section_alone()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["App/App.csproj"] = "<Project />" });

        Assert.Equal(0, Init(repo, out var output));

        var text = File.ReadAllText(repo.Full("AGENTS.md"));
        Assert.StartsWith(InstructionsFile.Begin, text, StringComparison.Ordinal);
        Assert.EndsWith(InstructionsFile.End + "\n", text, StringComparison.Ordinal);
        Assert.Contains("`fuse test`", text, StringComparison.Ordinal);
        Assert.Contains("wrote AGENTS.md", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_section_is_appended_once_and_a_later_run_replaces_only_it()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["App/App.csproj"] = "<Project />" });
        File.WriteAllText(repo.Full("AGENTS.md"), "# Rules\r\n\r\nKeep it short.\r\n");

        Init(repo, out _);
        var first = File.ReadAllText(repo.Full("AGENTS.md"));
        Assert.StartsWith("# Rules\r\n\r\nKeep it short.\r\n\r\n" + InstructionsFile.Begin + "\r\n", first, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\r", first, StringComparison.Ordinal);
        Assert.Equal(first.Split("\r\n").Length, first.Split('\n').Length);

        // An older section, and text after it, which stays.
        var start = first.IndexOf(InstructionsFile.Begin, StringComparison.Ordinal);
        File.WriteAllText(repo.Full("AGENTS.md"), first[..start] + InstructionsFile.Begin + "\r\nstale-section-text\r\n" + InstructionsFile.End + "\r\n\r\n## After\r\n");
        Init(repo, out _);
        var second = File.ReadAllText(repo.Full("AGENTS.md"));
        Assert.DoesNotContain("stale-section-text", second, StringComparison.Ordinal);
        Assert.EndsWith(InstructionsFile.End + "\r\n\r\n## After\r\n", second, StringComparison.Ordinal);
        Assert.Single(second.Split(InstructionsFile.Begin)[1..]);

        Init(repo, out var third);
        Assert.Contains("unchanged AGENTS.md", third, StringComparison.Ordinal);
    }

    [Fact]
    public void CLAUDE_md_gets_the_section_unless_it_imports_AGENTS_md_and_GEMINI_md_when_present()
    {
        using var plain = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["App/App.csproj"] = "<Project />", ["CLAUDE.md"] = "# Claude\n", ["GEMINI.md"] = "# Gemini\n" });
        using var importing = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["App/App.csproj"] = "<Project />", ["CLAUDE.md"] = "@AGENTS.md\n" });

        Assert.Equal(["AGENTS.md", "CLAUDE.md", "GEMINI.md"], InstructionsFile.FilesFor(plain.Root));
        Assert.Equal(["AGENTS.md"], InstructionsFile.FilesFor(importing.Root));
    }

    [Fact]
    public void Init_without_the_option_writes_no_instruction_file()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["App/App.csproj"] = "<Project />" });
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, InitCommand.Run(repo.Root.Path, output, error));
        Assert.False(File.Exists(repo.Full("AGENTS.md")));
    }
}
