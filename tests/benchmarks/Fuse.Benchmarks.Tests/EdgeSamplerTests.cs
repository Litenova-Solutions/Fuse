using Fuse.Benchmarks;
using Xunit;

namespace Fuse.Benchmarks.Tests;

// R6: the deterministic edge sampler used by the semantics corpus-adjudication mode.
public sealed class EdgeSamplerTests
{
    [Fact]
    public void Samples_at_most_perType_edges_of_each_type()
    {
        var edges = new List<SampledEdge>();
        for (var i = 0; i < 10; i++)
            edges.Add(new SampledEdge($"a{i}", $"b{i}", "di_resolves_to"));
        for (var i = 0; i < 3; i++)
            edges.Add(new SampledEdge($"r{i}", $"m{i}", "route_handles"));

        var sample = EdgeSampler.Sample(edges, perType: 4, seed: 1469);

        Assert.Equal(4, sample.Count(e => e.Type == "di_resolves_to"));
        Assert.Equal(3, sample.Count(e => e.Type == "route_handles")); // fewer than the cap, all kept
    }

    [Fact]
    public void Sampling_is_reproducible_for_a_fixed_seed()
    {
        var edges = Enumerable.Range(0, 20)
            .Select(i => new SampledEdge($"a{i}", $"b{i}", "di_injects"))
            .ToList();

        var first = EdgeSampler.Sample(edges, perType: 5, seed: 7);
        var second = EdgeSampler.Sample(edges, perType: 5, seed: 7);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Keeps_all_edges_when_under_the_cap()
    {
        var edges = new List<SampledEdge>
        {
            new("a", "b", "ef_entity"),
            new("c", "d", "ef_entity"),
        };

        var sample = EdgeSampler.Sample(edges, perType: 10, seed: 1);

        Assert.Equal(2, sample.Count);
    }
}
