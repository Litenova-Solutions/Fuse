using Fuse.Check;
using Fuse.Cli;
using Fuse.Dotnet;
using Fuse.Engine;
using Fuse.Hooks;
using Fuse.Protocol;
using Fuse.Testing;

namespace Fuse.Tests.Unit;

public class DiagnosticDeltaTests
{
    private static Diagnostic D(string path, int line, string id, string message) => new(path, line, 1, id, message);

    [Fact]
    public void Error_that_only_moved_lines_is_not_introduced()
    {
        var introduced = DiagnosticDelta.Introduced([D("a.cs", 20, "CS0103", "x")], [D("a.cs", 10, "CS0103", "x")]);
        Assert.Empty(introduced);
    }

    [Fact]
    public void Extra_copy_of_an_existing_error_is_introduced()
    {
        var introduced = DiagnosticDelta.Introduced([D("a.cs", 1, "CS0103", "x"), D("a.cs", 2, "CS0103", "x")], [D("a.cs", 1, "CS0103", "x")]);
        Assert.Single(introduced);
    }

    [Fact]
    public void Different_message_or_file_is_introduced()
    {
        var introduced = DiagnosticDelta.Introduced([D("a.cs", 1, "CS0103", "y"), D("b.cs", 1, "CS0103", "x")], [D("a.cs", 1, "CS0103", "x")]);
        Assert.Equal(2, introduced.Count());
    }
}
