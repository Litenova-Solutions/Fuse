using Fuse.Check;
using Fuse.Cli;
using Fuse.Dotnet;
using Fuse.Engine;
using Fuse.Hooks;
using Fuse.Protocol;
using Fuse.Testing;

namespace Fuse.Tests.Unit;

public class TestFilterTests
{
    [Fact]
    public void Joins_and_escapes_patterns()
    {
        var filter = TestPlanner.Filter(["Ns.T.Method", "Ns.Outer+Inner.Case(1)"]);
        Assert.Equal("FullyQualifiedName~Ns.Outer+Inner.Case\\(1\\)|FullyQualifiedName~Ns.T.Method", filter);
    }

    [Fact]
    public void Collapses_to_classes_then_to_everything_when_too_long()
    {
        var many = Enumerable.Range(0, 400).Select(i => $"Ns.Tests.SomeFairlyLongClassName.Method{i}").ToList();
        Assert.Equal("FullyQualifiedName~Ns.Tests.SomeFairlyLongClassName.", TestPlanner.Filter(many));

        var classes = Enumerable.Range(0, 400).Select(i => $"Ns.Tests.SomeFairlyLongClassName{i}.Method").ToList();
        Assert.Null(TestPlanner.Filter(classes));
    }
}
