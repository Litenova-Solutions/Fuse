using Fuse.Testing;
using Fuse.Testing.Model;

namespace Fuse.Tests.Unit;

/// <summary>
///     The selected and total counts in the summary come from these, without test discovery, so the count a selection
///     gets has to match what its filter runs.
/// </summary>
public class TestCounterTests
{
    private static readonly string[] Tests = ["Ns.CalcTests.Adds", "Ns.CalcTests.Multiplies", "Ns.Outer+Inner.Case", "Ns.GreeterTests.Greets"];

    [Fact]
    public void A_whole_selection_counts_every_test()
    {
        Assert.Equal(4, TestCounter.Count(Tests, new TestSelection.Whole("App uses the changed code and runs behind an application host")));
    }

    [Fact]
    public void A_methods_selection_counts_each_test_a_pattern_matches_once()
    {
        // The class prefix and the method name both match Multiplies, which is still one test.
        var selection = new TestSelection.Methods(["Ns.CalcTests.", "Ns.CalcTests.Multiplies", "Ns.Outer+Inner."]);
        Assert.Equal(3, TestCounter.Count(Tests, selection));
    }
}
