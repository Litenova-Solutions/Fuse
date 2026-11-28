using Fuse.Dotnet.Test;
using Fuse.Operations;
using Fuse.Operations.Test.Rendering;
namespace Fuse.Tests.Unit;

/// <summary>Every failure prints its own FAILED line, including the ones past the detail budget.</summary>
public class OverflowFailureLineTests
{
    [Fact]
    public void Fifteen_failures_render_fifteen_failed_lines()
    {
        var outcome = new TestRunSummary(0, 15, 0, [.. Enumerable.Range(0, 15).Select(i => new TestFailure($"Lib.Tests.CalcTests.Test{i}", $"boom {i}", []))]);

        var result = TestResultRenderer.Render(outcome, "ran 15 test(s)", 2.0);

        Assert.Equal(Outcome.ProblemsFound, result.Outcome);
        var lines = result.Text.Split('\n');
        Assert.Equal(15, lines.Count(l => l.Contains("FAILED ", StringComparison.Ordinal)));
        Assert.Contains(lines, l => l == "FAILED Lib.Tests.CalcTests.Test0");
        Assert.Equal("also failed (5):", lines.First(l => l.StartsWith("also failed (", StringComparison.Ordinal)));
        foreach (var i in Enumerable.Range(10, 5))
            Assert.Contains(lines, l => l == $"  FAILED Lib.Tests.CalcTests.Test{i}");
        Assert.StartsWith("fuse: 15 failed, 0 passed in 2.0 s; ran 15 test(s)", lines[^1], StringComparison.Ordinal);
    }
}
