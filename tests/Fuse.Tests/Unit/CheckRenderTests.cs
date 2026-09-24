using Fuse.Check;
using Fuse.Cli;
using Fuse.Dotnet;
using Fuse.Engine;
using Fuse.Hooks;
using Fuse.Protocol;
using Fuse.Testing;

namespace Fuse.Tests.Unit;

public class CheckRenderTests
{
    [Fact]
    public void Clean_report_says_so_with_scope()
    {
        var result = CheckOperation.Render(new CheckReport([], 3, [], ["Lib"], 2, false));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("fuse: no new errors (3 file(s) checked; Lib declarations changed, 2 dependent project(s) checked)", result.Text);
    }

    [Fact]
    public void Errors_are_listed_in_canonical_form_and_capped()
    {
        var diagnostics = Enumerable.Range(1, 25).Select(i => new Diagnostic($"src/F{i}.cs", i, 2, "CS0103", "The name 'x' does not exist in the current context")).ToArray();
        var result = CheckOperation.Render(new CheckReport(diagnostics, 25, ["App"], [], 0, false));
        Assert.Equal(1, result.ExitCode);
        var lines = result.Text.Split('\n');
        Assert.Equal(21, lines.Length);
        Assert.Equal("src/F1.cs(1,2): error CS0103: The name 'x' does not exist in the current context", lines[0]);
        Assert.Equal("fuse: 25 new error(s) in 25 file(s), first 20 shown (App)", lines[^1]);
    }
}
