using Fuse.Workspace;
using Xunit;

namespace Fuse.Workspace.Tests;

// T1: the covering-subset filter expression. It selects exactly the named tests, dedups, escapes operator
// characters in parameterized names, and yields empty for an empty set (which the caller treats as "run nothing").
public sealed class TestFilterBuilderTests
{
    [Fact]
    public void Builds_a_disjunction_of_fully_qualified_names()
    {
        var filter = TestFilterBuilder.Build(["Ns.A.Test1", "Ns.B.Test2"]);
        Assert.Equal("FullyQualifiedName=Ns.A.Test1|FullyQualifiedName=Ns.B.Test2", filter);
    }

    [Fact]
    public void An_empty_set_yields_an_empty_filter()
    {
        Assert.Equal(string.Empty, TestFilterBuilder.Build([]));
        Assert.Equal(string.Empty, TestFilterBuilder.Build(["", "   "]));
    }

    [Fact]
    public void Deduplicates_repeated_names()
    {
        var filter = TestFilterBuilder.Build(["Ns.A.Test1", "Ns.A.Test1"]);
        Assert.Equal("FullyQualifiedName=Ns.A.Test1", filter);
    }

    [Fact]
    public void BuildContains_selects_every_test_in_the_covering_types()
    {
        var filter = TestFilterBuilder.BuildContains(["Ns.OrderServiceTests", "Ns.BasketTests"]);
        Assert.Equal("FullyQualifiedName~Ns.OrderServiceTests|FullyQualifiedName~Ns.BasketTests", filter);
    }

    [Fact]
    public void BuildContains_of_an_empty_set_yields_an_empty_filter()
    {
        Assert.Equal(string.Empty, TestFilterBuilder.BuildContains([]));
    }

    [Fact]
    public void Escapes_operator_characters_in_a_parameterized_name()
    {
        // A theory/parameterized display name can carry parentheses and other operator chars; they must be escaped
        // so the disjunction is not misparsed.
        var filter = TestFilterBuilder.Build(["Ns.A.Test(x=1)"]);
        Assert.Equal(@"FullyQualifiedName=Ns.A.Test\(x\=1\)", filter);
    }
}
