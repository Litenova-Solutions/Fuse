using Fuse.Testing;
using Fuse.Testing.Model;

namespace Fuse.Tests.Unit;

public class TestFilterTests
{
    [Fact]
    public void Joins_and_escapes_patterns()
    {
        var filter = TestFilter.For(Methods("Ns.T.Method", "Ns.Outer+Inner.Case(1)"));
        Assert.Equal("FullyQualifiedName~Ns.Outer+Inner.Case\\(1\\)|FullyQualifiedName~Ns.T.Method", filter);
    }

    [Fact]
    public void Collapses_to_classes_then_to_everything_when_too_long()
    {
        var many = Enumerable.Range(0, 400).Select(i => $"Ns.Tests.SomeFairlyLongClassName.Method{i}").ToArray();
        Assert.Equal("FullyQualifiedName~Ns.Tests.SomeFairlyLongClassName.", TestFilter.For(Methods(many)));

        var classes = Enumerable.Range(0, 400).Select(i => $"Ns.Tests.SomeFairlyLongClassName{i}.Method").ToArray();
        Assert.Null(TestFilter.For(Methods(classes)));
    }

    [Fact]
    public void A_filter_of_8000_characters_is_kept_and_one_longer_collapses()
    {
        // "FullyQualifiedName~" is 19 characters, so a pattern of 7,981 makes a filter of exactly 8,000.
        var longest = "Ns.C." + new string('M', 7981 - "Ns.C.".Length);
        Assert.Equal(8000, TestFilter.For(Methods(longest))?.Length);
        Assert.Equal("FullyQualifiedName~Ns.C.", TestFilter.For(Methods(longest + "M")));
    }

    [Fact]
    public void Escapes_every_operator_of_the_filter_syntax()
    {
        Assert.Equal(@"FullyQualifiedName~Ns.C.M\\\(\)\&\|\=\!\~", TestFilter.For(Methods(@"Ns.C.M\()&|=!~")));
    }

    [Fact]
    public void A_whole_selection_has_no_filter()
    {
        Assert.Null(TestFilter.For(new TestSelection.Whole("a test file without classes changed")));
    }

    private static TestSelection.Methods Methods(params string[] patterns) => new([.. patterns]);
}
