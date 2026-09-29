using System.Text;
using System.Text.Json.Nodes;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

/// <summary>
///     The product in a repository whose path and file names are not ASCII. A harness writes its payload as UTF-8 over
///     standard input and names the file it edited; if any of that is decoded with the console's code page instead, the
///     hook silently reports nothing and the agent believes its edit is clean.
/// </summary>
public class NonAsciiPathTests
{
    // A Windows file name cannot hold a quote (System.IO.File rejects it), so the odd name is non-ASCII plus a name
    // with a trailing space, which git C-quotes in porcelain output for the same reason a quote would.
    private const string OddName = "ÆøÅ.cs";

    private static string Payload(string cwd, string file) => new JsonObject
    {
        ["hook_event_name"] = "PostToolUse",
        ["tool_name"] = "Edit",
        ["cwd"] = cwd,
        ["tool_input"] = new JsonObject { ["file_path"] = file },
    }.ToJsonString();

    [Fact]
    public async Task Check_reports_an_introduced_error_in_a_non_ascii_path()
    {
        using var repo = Create();
        try
        {
            repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
            var result = await FuseProcess.RunAsync(repo.Path, null, "check", "Lib/Calc.cs");
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("no errors introduced", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await StopAsync(repo);
        }
    }

    [Fact]
    public async Task Check_reports_the_break_it_finds()
    {
        using var repo = Create();
        try
        {
            repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
            var result = await FuseProcess.RunAsync(repo.Path, null, "check", "Lib/Calc.cs");
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Lib.Tests/CalcTests.cs", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await StopAsync(repo);
        }
    }

    [Fact]
    public async Task The_post_edit_hook_reports_the_break_from_a_utf8_payload()
    {
        using var repo = Create();
        try
        {
            repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
            var result = await FuseProcess.RunAsync(repo.Path, Payload(repo.Path, repo.Full("Lib/Calc.cs")), "hook", "claude", "post-edit");
            Assert.Equal(2, result.ExitCode);
            Assert.Contains("Lib.Tests/CalcTests.cs", result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            await StopAsync(repo);
        }
    }

    [Fact]
    public async Task The_post_edit_hook_says_nothing_when_the_edit_is_clean()
    {
        using var repo = Create();
        try
        {
            repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
            var result = await FuseProcess.RunAsync(repo.Path, Payload(repo.Path, repo.Full("Lib/Calc.cs")), "hook", "claude", "post-edit");
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("", result.Stdout + result.Stderr);
        }
        finally
        {
            await StopAsync(repo);
        }
    }

    [Fact]
    public async Task A_hook_payload_that_is_not_json_is_logged_and_still_exits_zero()
    {
        using var repo = Create();
        try
        {
            var result = await FuseProcess.RunAsync(repo.Path, "not json at all", "hook", "claude", "post-edit");
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("", result.Stdout + result.Stderr);
            var log = Path.Combine(repo.Root.StateDirectory, "hook.log");
            Assert.True(File.Exists(log), "the parse failure should be in hook.log, not silent");
            Assert.Contains("not valid JSON", await File.ReadAllTextAsync(log, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
        finally
        {
            await StopAsync(repo);
        }
    }

    [Fact]
    public async Task A_file_with_a_non_ascii_name_is_found_and_reported_by_its_real_name()
    {
        using var repo = Create();
        try
        {
            repo.Write($"Lib/{OddName}", "namespace Lib;\n\npublic static class Odd\n{\n    public static int One() => 1;\n}\n");
            repo.Replace($"Lib/{OddName}", "=> 1;", "=> undefinedValue;");

            var result = await FuseProcess.RunAsync(repo.Path, null, "check");

            // git spells this name "Lib/\303\206\303\270\303\205.cs" unless core.quotepath is off. The product reads
            // the name from the repository, so what it prints is the name the file actually has.
            Assert.Equal(1, result.ExitCode);
            Assert.Contains($"Lib/{OddName}", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("CS0103", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await StopAsync(repo);
        }
    }

    [Fact]
    public async Task A_file_whose_name_needs_quoting_is_tracked_as_changed()
    {
        using var repo = Create();
        try
        {
            // git C-quotes this name in porcelain output; the product has to read the path, not git's spelling of it.
            repo.Write("Lib/trailing .cs", "namespace Lib;\n\npublic static class Trailing\n{\n    public static int One() => 1;\n}\n");
            repo.Replace("Lib/trailing .cs", "=> 1;", "=> 1 + 1;");
            var result = await FuseProcess.RunAsync(repo.Path, null, "check");
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("no errors introduced", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await StopAsync(repo);
        }
    }

    [Fact]
    public async Task The_test_run_reaches_the_tests_in_a_non_ascii_path()
    {
        using var repo = Create();
        try
        {
            repo.Replace("Lib/Calc.cs", "a * b;", "a * undefinedValue;");
            var result = await FuseProcess.RunAsync(repo.Path, null, "test");
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("CS0103", result.Stdout + result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            await StopAsync(repo);
        }
    }

    private static FixtureRepo Create() => FixtureRepo.CreateStandard(FixtureRepo.NewNonAsciiDirectory());

    private static Task StopAsync(FixtureRepo repo) => FuseProcess.StopEngineAsync(repo.Root);
}
