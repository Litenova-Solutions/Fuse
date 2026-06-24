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
        Assert.True(options.QueryExpansion);
        Assert.True(options.BudgetAwareExpansion);
    }

    [Fact]
    public void Defaults_DenseRerankOffAndNoChurnPrior()
    {
        var options = new ExperimentalOptions();
        Assert.False(options.DenseRerank);
        Assert.Equal(0, options.GitChurnWeight);
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
