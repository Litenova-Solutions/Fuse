using Fuse.Fusion;

namespace Fuse.Fusion.Tests;

// Verifies that experimental knobs come from the request, with environment variables as an override applied at
// resolution, so a run's behavior follows its resolved options rather than ambient state.
public sealed class ExperimentalOptionsTests
{
    [Fact]
    public void Defaults_AreConservative()
    {
        var options = new ExperimentalOptions();

        Assert.Equal(0.15, options.CentralityWeight);
        Assert.Equal(0.5, options.HopDecay);
        Assert.Equal(0.2, options.ExpansionWeight);
        Assert.True(options.QueryExpansion);
        Assert.True(options.BudgetAwareExpansion);
    }

    [Fact]
    public void ResolveFromEnvironment_HopDecay_ParsesWithinRange()
    {
        var resolved = WithEnvironment(
            ("FUSE_HOP_DECAY", "0.6"),
            ("FUSE_CENTRALITY_WEIGHT", null),
            () => ExperimentalOptions.ResolveFromEnvironment());

        Assert.Equal(0.6, resolved.HopDecay);
    }

    [Fact]
    public void ResolveFromEnvironment_HopDecayOutOfRange_KeepsConfigured()
    {
        // The decay factor must be in (0, 1]; an out-of-range value is ignored so a bad override cannot
        // silently invert the ranking (a value above 1 would amplify distant neighbours over seeds).
        var configured = new ExperimentalOptions { HopDecay = 0.5 };

        var resolvedHigh = WithEnvironment(
            ("FUSE_HOP_DECAY", "1.5"),
            ("FUSE_CENTRALITY_WEIGHT", null),
            () => ExperimentalOptions.ResolveFromEnvironment(configured));
        var resolvedZero = WithEnvironment(
            ("FUSE_HOP_DECAY", "0"),
            ("FUSE_CENTRALITY_WEIGHT", null),
            () => ExperimentalOptions.ResolveFromEnvironment(configured));

        Assert.Equal(0.5, resolvedHigh.HopDecay);
        Assert.Equal(0.5, resolvedZero.HopDecay);
    }

    [Fact]
    public void ResolveFromEnvironment_ExpansionWeight_Parses()
    {
        var resolved = WithEnvironment(
            ("FUSE_EXPANSION_WEIGHT", "0.35"),
            ("FUSE_CENTRALITY_WEIGHT", null),
            () => ExperimentalOptions.ResolveFromEnvironment());

        Assert.Equal(0.35, resolved.ExpansionWeight);
    }

    [Fact]
    public void Defaults_DenseRerankOffAndNoChurnPrior()
    {
        var options = new ExperimentalOptions();
        Assert.False(options.DenseRerank);
        Assert.Equal(0, options.GitChurnWeight);
        Assert.False(options.SketchHugeFiles);
        Assert.True(options.DowngradeBeforeDrop);
    }

    [Fact]
    public void ResolveFromEnvironment_DowngradeDropOff_Disables()
    {
        var resolved = WithEnvironment(
            ("FUSE_DOWNGRADE_DROP", "0"),
            ("FUSE_CENTRALITY_WEIGHT", null),
            () => ExperimentalOptions.ResolveFromEnvironment());

        Assert.False(resolved.DowngradeBeforeDrop);
    }

    [Fact]
    public void ResolveFromEnvironment_ThesaurusOn_Enables()
    {
        var resolved = WithEnvironment(
            ("FUSE_THESAURUS", "1"),
            ("FUSE_CENTRALITY_WEIGHT", null),
            () => ExperimentalOptions.ResolveFromEnvironment());

        Assert.True(resolved.DistributionalThesaurus);
    }

    [Fact]
    public void ResolveFromEnvironment_ProximityOn_Enables()
    {
        var resolved = WithEnvironment(
            ("FUSE_PROXIMITY", "1"),
            ("FUSE_CENTRALITY_WEIGHT", null),
            () => ExperimentalOptions.ResolveFromEnvironment());

        Assert.True(resolved.ProximityEdges);
    }

    [Fact]
    public void ResolveFromEnvironment_MemberLevelOn_Enables()
    {
        var resolved = WithEnvironment(
            ("FUSE_MEMBER_LEVEL", "1"),
            ("FUSE_CENTRALITY_WEIGHT", null),
            () => ExperimentalOptions.ResolveFromEnvironment());

        Assert.True(resolved.MemberLevelRetrieval);
    }

    [Fact]
    public void ResolveFromEnvironment_GitChurnWeight_Parses()
    {
        var resolved = WithEnvironment(
            ("FUSE_GIT_CHURN_WEIGHT", "0.2"),
            ("FUSE_CENTRALITY_WEIGHT", null),
            () => ExperimentalOptions.ResolveFromEnvironment());

        Assert.Equal(0.2, resolved.GitChurnWeight);
    }

    [Fact]
    public void ResolveFromEnvironment_BudgetExpansionOff_Disables()
    {
        var resolved = WithEnvironment(
            ("FUSE_BUDGET_EXPANSION", "0"),
            ("FUSE_CENTRALITY_WEIGHT", null),
            () => ExperimentalOptions.ResolveFromEnvironment());

        Assert.False(resolved.BudgetAwareExpansion);
    }

    [Fact]
    public void ResolveFromEnvironment_NoEnv_PassesConfiguredValuesThrough()
    {
        var configured = new ExperimentalOptions { CentralityWeight = 0.42, QueryExpansion = false };

        var resolved = WithoutEnvironment(() => ExperimentalOptions.ResolveFromEnvironment(configured));

        Assert.Equal(0.42, resolved.CentralityWeight);
        Assert.False(resolved.QueryExpansion);
    }

    [Fact]
    public void ResolveFromEnvironment_Null_ReturnsDefaults()
    {
        var resolved = WithoutEnvironment(() => ExperimentalOptions.ResolveFromEnvironment());

        Assert.Equal(0.15, resolved.CentralityWeight);
        Assert.True(resolved.QueryExpansion);
    }

    [Fact]
    public void ResolveFromEnvironment_EnvironmentOverridesConfiguredValues()
    {
        var configured = new ExperimentalOptions { CentralityWeight = 0.5, QueryExpansion = true };

        var resolved = WithEnvironment(
            ("FUSE_CENTRALITY_WEIGHT", "0"),
            ("FUSE_QUERY_EXPANSION", "0"),
            () => ExperimentalOptions.ResolveFromEnvironment(configured));

        Assert.Equal(0, resolved.CentralityWeight);
        Assert.False(resolved.QueryExpansion);
    }

    // Runs the resolver with the two experimental environment variables cleared, restoring them after, so the
    // test is deterministic regardless of the host environment and leaves no global state behind.
    private static T WithoutEnvironment<T>(Func<T> action) =>
        WithEnvironment(("FUSE_CENTRALITY_WEIGHT", null), ("FUSE_QUERY_EXPANSION", null), action);

    private static T WithEnvironment<T>(
        (string Name, string? Value) first,
        (string Name, string? Value) second,
        Func<T> action)
    {
        var originalFirst = Environment.GetEnvironmentVariable(first.Name);
        var originalSecond = Environment.GetEnvironmentVariable(second.Name);
        try
        {
            Environment.SetEnvironmentVariable(first.Name, first.Value);
            Environment.SetEnvironmentVariable(second.Name, second.Value);
            return action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(first.Name, originalFirst);
            Environment.SetEnvironmentVariable(second.Name, originalSecond);
        }
    }
}
