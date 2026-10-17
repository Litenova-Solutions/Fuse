using Fuse.Check;
using Fuse.Check.Model;

namespace Fuse.Tests.Unit;

public class ErrorDeltaTests
{
    private static CompilerError E(string path, int line, string id, string message) => new(path, line, 1, id, message);

    [Fact]
    public void Error_that_only_moved_lines_is_not_introduced()
    {
        var introduced = ErrorDelta.Introduced([E("a.cs", 20, "CS0103", "x")], [E("a.cs", 10, "CS0103", "x")]);
        Assert.Empty(introduced);
    }

    [Fact]
    public void Extra_copy_of_an_existing_error_is_introduced()
    {
        var introduced = ErrorDelta.Introduced([E("a.cs", 1, "CS0103", "x"), E("a.cs", 2, "CS0103", "x")], [E("a.cs", 1, "CS0103", "x")]);
        Assert.Single(introduced);
    }

    [Fact]
    public void Different_message_or_file_is_introduced()
    {
        var introduced = ErrorDelta.Introduced([E("a.cs", 1, "CS0103", "y"), E("b.cs", 1, "CS0103", "x")], [E("a.cs", 1, "CS0103", "x")]);
        Assert.Equal(2, introduced.Count());
    }

    [Fact]
    public void Against_a_HEAD_whose_analyzers_did_not_finish_only_compiler_errors_are_compared()
    {
        CompilerError[] current = [E("a.cs", 1, "CS0103", "x"), new("a.cs", 2, 1, "CA1825", "y", FromAnalyzer: true)];

        var (unknown, leftOut) = ErrorDelta.Introduced(current, [], baselineAnalyzersFinished: false);
        var (known, nothingLeftOut) = ErrorDelta.Introduced(current, [], baselineAnalyzersFinished: true);

        Assert.Equal(["CS0103"], unknown.Select(e => e.Id));
        Assert.True(leftOut);
        Assert.Equal(["CS0103", "CA1825"], known.Select(e => e.Id));
        Assert.False(nothingLeftOut);
    }

    [Fact]
    public void A_HEAD_whose_analyzers_did_not_finish_leaves_out_nothing_when_no_analyzer_error_is_introduced()
    {
        var (introduced, leftOut) = ErrorDelta.Introduced([new CompilerError("a.cs", 2, 1, "CA1825", "y", FromAnalyzer: true)], [new CompilerError("a.cs", 9, 1, "CA1825", "y", FromAnalyzer: true)], baselineAnalyzersFinished: false);

        Assert.Empty(introduced);
        Assert.False(leftOut);
    }
}
