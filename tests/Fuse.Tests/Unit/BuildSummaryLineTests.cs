using Fuse.Dotnet.Processes;
using Fuse.Operations.Build;
namespace Fuse.Tests.Unit;

/// <summary>The build summary line: the warning count it always carries when the log was read, and the command that prints every error when errors pass the cap.</summary>
public class BuildSummaryLineTests
{
    [Fact]
    public void A_succeeded_build_with_no_warnings_prints_zero_warnings()
    {
        const string log = "Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n";

        var answer = BuildOperation.Render(new ProcessResult(0, ""), "/repo", 3, "build", BuildOptions.Default, log: log);

        Assert.Equal("fuse: build succeeded in 3.0 s; 0 warning(s)", answer.Text);
    }

    [Fact]
    public void A_succeeded_build_with_two_warnings_prints_two_warnings()
    {
        const string log = "Build succeeded.\n    2 Warning(s)\n    0 Error(s)\n";

        var answer = BuildOperation.Render(new ProcessResult(0, ""), "/repo", 3, "build", BuildOptions.Default, log: log);

        Assert.Equal("fuse: build succeeded in 3.0 s; 2 warning(s)", answer.Text);
    }

    [Fact]
    public void A_capped_failure_names_the_rerun_with_the_original_arguments()
    {
        var output = string.Join('\n', Enumerable.Range(1, 25).Select(i => $"src/F{i}.cs({i},2): error CS0103: The name 'x' does not exist in the current context"));

        var answer = BuildOperation.Render(new ProcessResult(1, output), "/repo", 3, "build", new BuildOptions(5, false, false), "/state/last-build-results.txt", passthrough: ["apps/api/App.slnx", "-c", "Release"]);

        Assert.Equal("fuse: build failed with 25 error(s), first 5 shown in 3.0 s; all 25 in /state/last-build-results.txt; fuse build apps/api/App.slnx -c Release --all-errors for every error", answer.Text.Split('\n')[^1]);
    }

    [Fact]
    public void An_argument_with_a_space_is_quoted_in_the_rerun()
    {
        var output = string.Join('\n', Enumerable.Range(1, 25).Select(i => $"src/F{i}.cs({i},2): error CS0103: The name 'x' does not exist in the current context"));

        var answer = BuildOperation.Render(new ProcessResult(1, output), "/repo", 3, "build", new BuildOptions(5, false, false), "/state/last-build-results.txt", passthrough: ["my dir/App.slnx"]);

        Assert.Contains("25 error(s), first 5 shown", answer.Text, StringComparison.Ordinal);
        Assert.Contains("fuse build \"my dir/App.slnx\" --all-errors for every error", answer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_changed_build_rerun_keeps_the_changed_flag()
    {
        var output = string.Join('\n', Enumerable.Range(1, 25).Select(i => $"src/F{i}.cs({i},2): error CS0103: The name 'x' does not exist in the current context"));

        var answer = BuildOperation.Render(new ProcessResult(1, output), "/repo", 3, "build", new BuildOptions(5, false, false), "/state/last-build-results.txt", passthrough: ["-c", "Release"], changed: true);

        Assert.Contains("fuse build -c Release --changed --all-errors for every error", answer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_below_the_cap_prints_no_rerun()
    {
        var answer = BuildOperation.Render(new ProcessResult(1, "App/Program.cs(3,30): error CS1061: no Add"), "/repo", 3, "build", new BuildOptions(20, false, false), "/state/last-build-results.txt", passthrough: ["apps/api/App.slnx"]);

        Assert.Equal("App/Program.cs(3,30): error CS1061: no Add\nfuse: build failed with 1 error(s) in 3.0 s", answer.Text);
        Assert.DoesNotContain("--all-errors", answer.Text, StringComparison.Ordinal);
    }
}
