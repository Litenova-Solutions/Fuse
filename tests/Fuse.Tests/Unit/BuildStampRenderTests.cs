using Fuse.Dotnet;
using Fuse.Operations;

namespace Fuse.Tests.Unit;

public class BuildStampRenderTests
{
    private const string Stamp = "; tree 9f2c41aa07b3 over 87 changed file(s)";

    [Fact]
    public void A_success_summary_carries_the_stamp()
    {
        var answer = BuildOperation.Render(new ProcessResult(0, ""), "/repo", 3, "build", BuildOptions.Default, treeStamp: Stamp);

        Assert.Equal("fuse: build succeeded in 3.0 s; tree 9f2c41aa07b3 over 87 changed file(s)", answer.Text);
    }

    [Fact]
    public void A_failure_summary_carries_the_stamp_before_the_file()
    {
        var output = string.Join('\n', Enumerable.Range(1, 25).Select(i => $"src/F{i}.cs({i},2): error CS0103: The name 'x' does not exist in the current context"));

        var answer = BuildOperation.Render(new ProcessResult(1, output), "/repo", 3, "build", new BuildOptions(5, false, false), "/state/last-build-results.txt", treeStamp: Stamp);

        Assert.Equal("fuse: build failed with 25 error(s), first 5 shown in 3.0 s; tree 9f2c41aa07b3 over 87 changed file(s); all 25 in /state/last-build-results.txt", answer.Text.Split('\n')[^1]);
    }

    [Fact]
    public void A_changed_build_summary_names_the_combined_file_count()
    {
        var output = string.Join('\n', Enumerable.Range(1, 25).Select(i => $"src/F{i}.cs({i},2): error CS0103: The name 'x' does not exist in the current context"));

        var answer = BuildOperation.Render(new ProcessResult(1, output), "/repo", 3, "build (Lib)", new BuildOptions(5, false, false), "/state/last-build-results.txt", "", Stamp, 40);

        Assert.Equal("fuse: build (Lib) failed with 25 error(s), first 5 shown in 3.0 s; tree 9f2c41aa07b3 over 87 changed file(s); all 40 in /state/last-build-results.txt", answer.Text.Split('\n')[^1]);
    }

    [Fact]
    public void A_failure_without_an_error_line_carries_the_stamp()
    {
        var answer = BuildOperation.Render(new ProcessResult(2, "the build broke without an error line"), "/repo", 3, "build", BuildOptions.Default, treeStamp: Stamp);

        Assert.StartsWith("fuse: build failed (exit code 2) in 3.0 s; tree 9f2c41aa07b3 over 87 changed file(s)", answer.Text.Split('\n')[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void No_stamp_prints_without_one()
    {
        var answer = BuildOperation.Render(new ProcessResult(0, ""), "/repo", 3, "build", BuildOptions.Default);

        Assert.DoesNotContain("tree", answer.Text, StringComparison.Ordinal);
    }
}
