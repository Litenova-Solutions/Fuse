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
        Assert.True(options.TieredEmission);
        Assert.False(options.SketchHugeFiles);
        Assert.True(options.DowngradeBeforeDrop);
        Assert.False(options.ProximityEdges);
        Assert.False(options.ProjectGraph);
    }

    [Fact]
    public void ResolveFromEnvironment_CentralityWeight_Parses()
    {
        var resolved = WithEnvironment(
            ("FUSE_CENTRALITY_WEIGHT", "0.42"),
            () => ExperimentalOptions.ResolveFromEnvironment());

        Assert.Equal(0.42, resolved.CentralityWeight);
    }

    [Fact]
    public void ResolveFromEnvironment_TieredEmissionOff_Disables()
    {
        var resolved = WithEnvironment(
            ("FUSE_TIERED_EMISSION", "0"),
            () => ExperimentalOptions.ResolveFromEnvironment());

        Assert.False(resolved.TieredEmission);
    }

    [Fact]
    public void ResolveFromEnvironment_DowngradeDropOff_Disables()
    {
        var resolved = WithEnvironment(
            ("FUSE_DOWNGRADE_DROP", "0"),
            () => ExperimentalOptions.ResolveFromEnvironment());

        Assert.False(resolved.DowngradeBeforeDrop);
    }

    [Fact]
    public void ResolveFromEnvironment_ProximityOn_Enables()
    {
        var resolved = WithEnvironment(
            ("FUSE_PROXIMITY", "1"),
            () => ExperimentalOptions.ResolveFromEnvironment());

        Assert.True(resolved.ProximityEdges);
    }

    [Fact]
    public void ResolveFromEnvironment_ProjectGraphOn_Enables()
    {
        var resolved = WithEnvironment(
            ("FUSE_PROJECT_GRAPH", "1"),
            () => ExperimentalOptions.ResolveFromEnvironment());

        Assert.True(resolved.ProjectGraph);
    }

    [Fact]
    public void ResolveFromEnvironment_SketchHugeOn_Enables()
    {
        var resolved = WithEnvironment(
            ("FUSE_SKETCH_HUGE", "1"),
            () => ExperimentalOptions.ResolveFromEnvironment());

        Assert.True(resolved.SketchHugeFiles);
    }

    [Fact]
    public void ResolveFromEnvironment_NoEnv_PassesConfiguredValuesThrough()
    {
        var configured = new ExperimentalOptions { CentralityWeight = 0.42, TieredEmission = false };

        var resolved = WithoutEnvironment(() => ExperimentalOptions.ResolveFromEnvironment(configured));

        Assert.Equal(0.42, resolved.CentralityWeight);
        Assert.False(resolved.TieredEmission);
    }

    [Fact]
    public void ResolveFromEnvironment_Null_ReturnsDefaults()
    {
        var resolved = WithoutEnvironment(() => ExperimentalOptions.ResolveFromEnvironment());

        Assert.Equal(0.15, resolved.CentralityWeight);
        Assert.True(resolved.TieredEmission);
    }

    [Fact]
    public void ResolveFromEnvironment_EnvironmentOverridesConfiguredValues()
    {
        var configured = new ExperimentalOptions { CentralityWeight = 0.5, TieredEmission = true };

        var resolved = WithEnvironment(
            ("FUSE_CENTRALITY_WEIGHT", "0"),
            ("FUSE_TIERED_EMISSION", "0"),
            () => ExperimentalOptions.ResolveFromEnvironment(configured));

        Assert.Equal(0, resolved.CentralityWeight);
        Assert.False(resolved.TieredEmission);
    }

    private static T WithoutEnvironment<T>(Func<T> action) =>
        WithEnvironment(("FUSE_CENTRALITY_WEIGHT", null), action);

    private static T WithEnvironment<T>((string Name, string? Value) env, Func<T> action)
    {
        var original = Environment.GetEnvironmentVariable(env.Name);
        try
        {
            Environment.SetEnvironmentVariable(env.Name, env.Value);
            return action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(env.Name, original);
        }
    }

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
