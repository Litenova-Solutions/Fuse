using System.Text.Json;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

public class CliHelpProcessTests
{
    [Theory]
    [InlineData("check", "fuse check [files...]")]
    [InlineData("test", "fuse test [dotnet test arguments]")]
    [InlineData("build", "fuse build [dotnet build arguments]")]
    [InlineData("stop", "fuse stop [--all]")]
    [InlineData("status", "fuse status")]
    [InlineData("session", "fuse session begin")]
    [InlineData("init", "fuse init [--instructions]")]
    [InlineData("uninit", "fuse uninit")]
    [InlineData("mcp", "fuse mcp")]
    // No hook case: the hook prints its own usage to stderr (see HookHelpProcessTests).
    [InlineData("engine", "fuse engine <root>")]
    [InlineData("version", "fuse --version")]
    public async Task Help_prints_the_subcommands_usage_and_runs_nothing(string command, string usage)
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });

        var result = await FuseProcess.RunAsync(repo.Path, null, command, "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(usage, result.Stdout, StringComparison.Ordinal);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task Short_help_and_help_after_a_file_print_help_too()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });

        var shortOption = await FuseProcess.RunAsync(repo.Path, null, "check", "-h");
        Assert.Equal(0, shortOption.ExitCode);
        Assert.Contains("fuse check [files...]", shortOption.Stdout, StringComparison.Ordinal);

        var afterFile = await FuseProcess.RunAsync(repo.Path, null, "check", "Lib/Calc.cs", "--help");
        Assert.Equal(0, afterFile.ExitCode);
        Assert.Contains("fuse check [files...]", afterFile.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_outside_a_repository_still_prints_help()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fuse-no-repo-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(directory);
        try
        {
            var result = await FuseProcess.RunAsync(directory, null, "build", "--help");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("fuse build [dotnet build arguments]", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Build_help_starts_no_engine_and_writes_no_state()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            var result = await FuseProcess.RunAsync(repo.Path, null, "build", "--help");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("fuse build [dotnet build arguments]", result.Stdout, StringComparison.Ordinal);
            Assert.False(FuseProcess.EngineRunning(repo.Root));
            Assert.False(Directory.Exists(repo.Root.StateDirectory));
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task An_unknown_check_option_is_a_usage_failure()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });

        var result = await FuseProcess.RunAsync(repo.Path, null, "check", "--bogus");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Contains("fuse: check takes files and [--max-errors <n>|--all-errors] [--json]; '--bogus' is not one", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("fuse check [files...]", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_near_miss_of_a_fuse_build_option_suggests_what_it_meant()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });

        var result = await FuseProcess.RunAsync(repo.Path, null, "build", "--max-erors", "5");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Contains("fuse: '--max-erors' is not a fuse build option; did you mean '--max-errors'?", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("fuse build [dotnet build arguments]", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_near_miss_of_a_fuse_test_option_suggests_what_it_meant()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });

        var result = await FuseProcess.RunAsync(repo.Path, null, "test", "--al");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Contains("fuse: '--al' is not a fuse test option; did you mean '--all'?", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_is_not_available_in_this_build()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });

        var result = await FuseProcess.RunAsync(repo.Path, null, "status");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("fuse: status is not available in this build", result.Stderr);
    }

    [Fact]
    public async Task Mcp_with_arguments_is_a_usage_failure()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });

        var result = await FuseProcess.RunAsync(repo.Path, null, "mcp", "--bogus");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("fuse mcp", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rewritten_test_says_it_ran_through_fuse()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            var result = await FuseProcess.RunAsync(repo.Path, null, "test", "--from-rewrite");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("fuse: dotnet test routed to fuse test (tests affected by your changes; fuse test --all runs every test)", result.Stderr.Split('\n')[0]);
            Assert.DoesNotContain("routed", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task A_rewritten_build_says_it_ran_through_fuse()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            var result = await FuseProcess.RunAsync(repo.Path, null, "build", "--from-rewrite");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("fuse: dotnet build routed to fuse build (first 20 errors; fuse build --all-errors for every error)", result.Stderr.Split('\n')[0]);
            Assert.Contains("fuse: build succeeded", result.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("routed", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task Build_max_errors_caps_the_answer_and_names_the_file_with_every_error()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");

            var result = await FuseProcess.RunAsync(repo.Path, null, "build", "--max-errors", "1");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("first 1 shown", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("; all 2 in ", result.Stdout, StringComparison.Ordinal);
            var file = Path.Combine(repo.Root.StateDirectory, "last-build-results.txt");
            Assert.Contains("App/Program.cs", File.ReadAllText(file), StringComparison.Ordinal);
            Assert.Contains("Lib.Tests/CalcTests.cs", File.ReadAllText(file), StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task Build_json_prints_one_object_per_error_with_the_summary_last()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");

            var result = await FuseProcess.RunAsync(repo.Path, null, "build", "--json");

            Assert.Equal(1, result.ExitCode);
            var lines = result.Stdout.Split('\n');
            Assert.True(lines.Length > 2, result.Stdout);
            foreach (var line in lines[..^1])
            {
                using var parsed = JsonDocument.Parse(line);
                Assert.True(parsed.RootElement.TryGetProperty("file", out _));
                Assert.True(parsed.RootElement.TryGetProperty("code", out _));
            }

            Assert.StartsWith("fuse: build failed with 2 error(s) in ", lines[^1], StringComparison.Ordinal);
            Assert.DoesNotContain("last-build-results.txt", lines[^1], StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task Build_warnings_lists_the_warnings()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            repo.Write("Lib/Legacy.cs", "namespace Lib;\n\npublic static class Legacy\n{\n    [System.Obsolete(\"use New\")]\n    public static int Old() => 1;\n}\n");
            repo.Replace("Lib/Calc.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => a + b + Legacy.Old();");

            var result = await FuseProcess.RunAsync(repo.Path, null, "build", "--warnings");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("  Lib/Calc.cs", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("warning CS0618", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("; 1 warning(s)", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task Check_json_and_max_errors_shape_the_answer()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");

            // The capped check runs first, so the uncapped one proves a kept capped answer is not reused for it.
            var capped = await FuseProcess.RunAsync(repo.Path, null, "check", "--max-errors", "1");
            Assert.Equal(1, capped.ExitCode);
            Assert.Contains("first 1 shown", capped.Stdout, StringComparison.Ordinal);
            Assert.Contains("last-check-results.txt", capped.Stdout, StringComparison.Ordinal);

            var json = await FuseProcess.RunAsync(repo.Path, null, "check", "--json");
            Assert.Equal(1, json.ExitCode);
            var lines = json.Stdout.Split('\n');
            Assert.Equal(3, lines.Length);
            using var first = JsonDocument.Parse(lines[0]);
            Assert.True(first.RootElement.TryGetProperty("file", out _));
            Assert.True(first.RootElement.TryGetProperty("cause", out _));
            Assert.Contains("error(s) introduced", lines[^1], StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task Check_after_two_dashes_treats_help_as_a_file()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            var result = await FuseProcess.RunAsync(repo.Path, null, "check", "--", "--help");

            Assert.Equal(2, result.ExitCode);
            Assert.StartsWith("fuse:", result.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("fuse check [files...]", result.Stdout + result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }
}
