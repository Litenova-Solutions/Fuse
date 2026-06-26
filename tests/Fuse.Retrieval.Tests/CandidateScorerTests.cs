using Fuse.Retrieval;
using Xunit;

namespace Fuse.Retrieval.Tests;

// P5.2: candidate score normalization, dedup, and ranking.
public sealed class CandidateScorerTests
{
    private readonly CandidateScorer _scorer = new();

    [Fact]
    public void MergesSameFileCandidatesAndCombinesScore()
    {
        var candidates = new[]
        {
            File("src/OrderService.cs", CandidateSource.FtsBody),   // 0.55
            File("src/OrderService.cs", CandidateSource.FtsPath),   // 0.70
        };

        var scored = _scorer.Score(candidates);

        var merged = Assert.Single(scored);
        // Noisy-or of 0.55 and 0.70 = 1 - (0.45 * 0.30) = 0.865.
        Assert.Equal(0.865, merged.Score, precision: 3);
        Assert.Equal(2, merged.Sources.Count);
    }

    [Fact]
    public void KeepsDistinctNodesSeparate()
    {
        var candidates = new[]
        {
            Node("type:App.OrderService", "src/OrderService.cs", CandidateSource.ServiceExact),
            Node("type:App.IOrderService", "src/IOrderService.cs", CandidateSource.SymbolExact),
        };

        var scored = _scorer.Score(candidates);

        Assert.Equal(2, scored.Count);
    }

    [Fact]
    public void RanksHigherScoreFirst()
    {
        var candidates = new[]
        {
            File("src/Low.cs", CandidateSource.FtsBody),        // 0.55
            File("src/High.cs", CandidateSource.DiffChangedFile), // 1.00
        };

        var scored = _scorer.Score(candidates);

        Assert.Equal("src/High.cs", scored[0].FilePath);
        Assert.Equal("src/Low.cs", scored[1].FilePath);
    }

    private static CandidateNode File(string path, CandidateSource source) =>
        new(string.Empty, path, "file", CandidateSourceWeights.Weight(source), source, [source.ToString()], 0);

    private static CandidateNode Node(string nodeId, string path, CandidateSource source) =>
        new(nodeId, path, "type", CandidateSourceWeights.Weight(source), source, [source.ToString()], 0);
}
