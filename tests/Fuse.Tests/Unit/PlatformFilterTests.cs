using System.Collections.Immutable;
using Fuse.Graph;
using Fuse.Testing;
using Fuse.Testing.Model;

namespace Fuse.Tests.Unit;

/// <summary>
///     A selection as each platform framework's filter: xUnit v3 one kind of name, spelled for how it runs, TUnit one
///     treenode expression, and past the bound whole classes, then no filter, so a run never holds fewer tests than
///     the selection names (#57).
/// </summary>
public class PlatformFilterTests
{
    private static TestSelection.Methods Methods(params string[] patterns) =>
        new(ImmutableHashSet.Create(StringComparer.Ordinal, patterns));

    [Fact]
    public void XUnit_methods_under_dotnet_test_take_two_dashes()
    {
        var (arguments, collapse) = PlatformFilter.For(TestFramework.XUnit, Methods("Ns.CalcTests.Multiplies", "Ns.CalcTests.Adds"), runner: true);

        Assert.Equal(["--filter-method", "Ns.CalcTests.Adds", "--filter-method", "Ns.CalcTests.Multiplies"], arguments);
        Assert.Equal(FilterCollapse.None, collapse);
    }

    [Fact]
    public void XUnit_methods_in_its_own_application_take_one_dash_and_keep_the_nested_plus()
    {
        var (arguments, _) = PlatformFilter.For(TestFramework.XUnit, Methods("Ns.Outer+Inner.Adds"), runner: false);

        Assert.Equal(["-method", "Ns.Outer+Inner.Adds"], arguments);
    }

    [Fact]
    public void XUnit_class_prefixes_take_class_filters()
    {
        var (arguments, collapse) = PlatformFilter.For(TestFramework.XUnit, Methods("Ns.CalcTests."), runner: true);

        Assert.Equal(["--filter-class", "Ns.CalcTests"], arguments);
        Assert.Equal(FilterCollapse.None, collapse);
    }

    [Fact]
    public void XUnit_mixed_methods_and_prefixes_widen_to_classes()
    {
        var (arguments, collapse) = PlatformFilter.For(TestFramework.XUnit, Methods("Ns.CalcTests.Adds", "Ns.ReportTests."), runner: false);

        Assert.Equal(["-class", "Ns.CalcTests", "-class", "Ns.ReportTests"], arguments);
        Assert.Equal(FilterCollapse.ToClasses, collapse);
    }

    [Fact]
    public void XUnit_methods_past_the_bound_collapse_to_their_classes_then_to_no_filter()
    {
        var oneClass = Enumerable.Range(0, PlatformFilter.MaxNames + 1).Select(i => $"Ns.CalcTests.Test{i}").ToArray();
        var (arguments, collapse) = PlatformFilter.For(TestFramework.XUnit, Methods(oneClass), runner: true);
        Assert.Equal(["--filter-class", "Ns.CalcTests"], arguments);
        Assert.Equal(FilterCollapse.ToClasses, collapse);

        var manyClasses = Enumerable.Range(0, PlatformFilter.MaxNames + 1).Select(i => $"Ns.Tests{i}.Test").ToArray();
        var (whole, wholeCollapse) = PlatformFilter.For(TestFramework.XUnit, Methods(manyClasses), runner: true);
        Assert.Empty(whole);
        Assert.Equal(FilterCollapse.ToWholeProject, wholeCollapse);
    }

    [Fact]
    public void TUnit_methods_of_one_class_share_one_expression()
    {
        var (arguments, collapse) = PlatformFilter.For(TestFramework.TUnit, Methods("Ns.CalcTests.Multiplies", "Ns.CalcTests.Adds"), runner: true);

        Assert.Equal(["--treenode-filter", "/*/Ns/CalcTests/(Adds|Multiplies)"], arguments);
        Assert.Equal(FilterCollapse.None, collapse);
    }

    [Fact]
    public void TUnit_one_method_needs_no_group_and_a_nested_class_names_its_innermost_name()
    {
        var (one, _) = PlatformFilter.For(TestFramework.TUnit, Methods("Ns.CalcTests.Adds"), runner: false);
        Assert.Equal(["--treenode-filter", "/*/Ns/CalcTests/Adds"], one);

        var (nested, _) = PlatformFilter.For(TestFramework.TUnit, Methods("Ns.Outer+Inner.Adds"), runner: false);
        Assert.Equal(["--treenode-filter", "/*/Ns/Inner/Adds"], nested);
    }

    [Fact]
    public void TUnit_several_classes_collapse_to_one_class_expression()
    {
        var (arguments, collapse) = PlatformFilter.For(TestFramework.TUnit, Methods("Ns.CalcTests.Adds", "Other.ReportTests.Shows"), runner: true);

        Assert.Equal(["--treenode-filter", "/*/*/(CalcTests|ReportTests)/*"], arguments);
        Assert.Equal(FilterCollapse.ToClasses, collapse);
    }

    [Fact]
    public void TUnit_a_class_prefix_of_one_class_stays_exact()
    {
        var (arguments, collapse) = PlatformFilter.For(TestFramework.TUnit, Methods("Ns.CalcTests."), runner: true);

        Assert.Equal(["--treenode-filter", "/*/Ns/CalcTests/*"], arguments);
        Assert.Equal(FilterCollapse.None, collapse);
    }

    [Fact]
    public void TUnit_classes_past_the_bound_run_the_project_whole()
    {
        var manyClasses = Enumerable.Range(0, PlatformFilter.MaxNames + 1).Select(i => $"Ns.Tests{i}.Test").ToArray();

        var (arguments, collapse) = PlatformFilter.For(TestFramework.TUnit, Methods(manyClasses), runner: true);

        Assert.Empty(arguments);
        Assert.Equal(FilterCollapse.ToWholeProject, collapse);
    }

    [Fact]
    public void A_whole_selection_has_no_arguments()
    {
        var (arguments, collapse) = PlatformFilter.For(TestFramework.TUnit, new TestSelection.Whole("reason"), runner: true);

        Assert.Empty(arguments);
        Assert.Equal(FilterCollapse.None, collapse);
    }
}
