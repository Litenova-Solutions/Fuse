using Fuse.Testing;
using Fuse.Testing.Model;

namespace Fuse.Tests.Unit;

public class TestFilterTests
{
    [Fact]
    public void Joins_and_escapes_patterns()
    {
        var filter = TestFilter.For(Methods("Ns.T.Method", "Ns.Outer+Inner.Case(1)"));
        Assert.Equal(new RunFilter("FullyQualifiedName~Ns.Outer+Inner.Case\\(1\\)|FullyQualifiedName~Ns.T.Method", 2, FilterCollapse.None), filter);
    }

    [Fact]
    public void A_filter_far_above_8000_characters_keeps_every_method()
    {
        // The filter goes into a runsettings file, so its length no longer collapses it.
        var many = Enumerable.Range(0, 400).Select(i => $"Ns.Tests.SomeFairlyLongClassName.Method{i}").ToArray();
        var filter = TestFilter.For(Methods(many));
        Assert.Equal((FilterCollapse.None, 400), Describe(filter));
        Assert.True(filter.Expression!.Length > 8000);
    }

    [Fact]
    public void A_filter_of_the_most_patterns_is_kept_and_one_more_collapses_to_classes()
    {
        var most = Enumerable.Range(0, TestFilter.MaxPatterns).Select(i => $"Ns.C{i % 10}.Method{i}").ToArray();
        Assert.Equal((FilterCollapse.None, TestFilter.MaxPatterns), Describe(TestFilter.For(Methods(most))));

        var filter = TestFilter.For(Methods([.. most, "Ns.C0.OneMore"]));
        Assert.Equal((FilterCollapse.ToClasses, 10), Describe(filter));
        Assert.Equal(string.Join('|', Enumerable.Range(0, 10).Select(i => $"FullyQualifiedName~Ns.C{i}.")), filter.Expression);
    }

    [Fact]
    public void Collapses_to_no_filter_when_the_classes_are_still_too_many()
    {
        var classes = Enumerable.Range(0, TestFilter.MaxPatterns + 1).Select(i => $"Ns.C{i}.Method").ToArray();
        Assert.Equal(new RunFilter(null, 0, FilterCollapse.ToWholeProject), TestFilter.For(Methods(classes)));
    }

    [Fact]
    public void Escapes_every_operator_of_the_filter_syntax()
    {
        Assert.Equal(@"FullyQualifiedName~Ns.C.M\\\(\)\&\|\=\!\~", TestFilter.For(Methods(@"Ns.C.M\()&|=!~")).Expression);
    }

    [Fact]
    public void A_whole_selection_has_no_filter_and_no_collapse()
    {
        Assert.Equal(new RunFilter(null, 0, FilterCollapse.None), TestFilter.For(new TestSelection.Whole("a test file without classes changed")));
    }

    private static (FilterCollapse, int) Describe(RunFilter filter) => (filter.Collapse, filter.Patterns);

    private static TestSelection.Methods Methods(params string[] patterns) => new([.. patterns]);
}
