using Fuse.Testing;
using Fuse.Testing.Model;

namespace Fuse.Tests.Unit;

public class TestFilterExcludeTests
{
    [Fact]
    public void Parenthesises_the_join_and_appends_one_inequality_per_excluded_test()
    {
        var filter = TestFilter.For(Methods("Ns.T.B", "Ns.T.A"), ["Ns.T.A"])!;

        Assert.Equal("(FullyQualifiedName~Ns.T.A|FullyQualifiedName~Ns.T.B)&FullyQualifiedName!=Ns.T.A", filter.Expression);
        Assert.Equal((3, FilterCollapse.None), (filter.Patterns, filter.Collapse));
    }

    [Fact]
    public void Escapes_operators_in_excluded_names()
    {
        var filter = TestFilter.For(Methods("Ns.T.Case(1)"), ["Ns.T.Case(1)", @"Ns.T.M\A"])!;

        Assert.Equal(@"(FullyQualifiedName~Ns.T.Case\(1\))&FullyQualifiedName!=Ns.T.Case\(1\)&FullyQualifiedName!=Ns.T.M\\A", filter!.Expression);
    }

    [Fact]
    public void Above_the_bound_whole_or_empty_selections_have_no_split_filter()
    {
        var most = Enumerable.Range(0, TestFilter.MaxPatterns).Select(i => $"Ns.C{i}.Method{i}").ToArray();
        Assert.Null(TestFilter.For(Methods(most), ["Ns.C0.Method0"]));
        Assert.Null(TestFilter.For(new TestSelection.Whole("a test file without classes changed"), ["Ns.T.A"]));
        Assert.Null(TestFilter.For(Methods(), ["Ns.T.A"]));
    }

    [Fact]
    public void No_exclusion_is_today_s_filter()
    {
        Assert.Equal(TestFilter.For(Methods("Ns.T.A")), TestFilter.For(Methods("Ns.T.A"), []));
    }

    private static TestSelection.Methods Methods(params string[] patterns) => new([.. patterns]);
}
