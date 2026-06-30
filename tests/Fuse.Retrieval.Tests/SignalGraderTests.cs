using Fuse.Retrieval;
using Xunit;

namespace Fuse.Retrieval.Tests;

// S3: the signal grader maps a scored candidate distribution to confident/partial/insufficient deterministically,
// from fixed thresholds, so the same scores always grade the same way.
public sealed class SignalGraderTests
{
    [Fact]
    public void ClearWinnerIsConfident()
    {
        var ranked = Ranked(0.95, 0.40, 0.30);

        Assert.Equal(SignalState.Confident, SignalGrader.Grade(ranked));
    }

    [Fact]
    public void TightStrongClusterIsConfident()
    {
        // Two strong candidates within the cluster band, then a clear drop: still confident, the cluster stands clear.
        var ranked = Ranked(0.90, 0.86, 0.30);

        Assert.Equal(SignalState.Confident, SignalGrader.Grade(ranked));
        Assert.Equal(2, SignalGrader.LeadingCluster(ranked).Count);
    }

    [Fact]
    public void SingleCandidateAtThresholdIsConfident()
    {
        var ranked = Ranked(0.55);

        Assert.Equal(SignalState.Confident, SignalGrader.Grade(ranked));
        Assert.Single(SignalGrader.LeadingCluster(ranked));
    }

    [Fact]
    public void FlatModerateDistributionIsPartial()
    {
        // A run of similar moderate scores with no separation: there is signal but no clear winner.
        var ranked = Ranked(0.58, 0.56, 0.55, 0.54, 0.52);

        Assert.Equal(SignalState.Partial, SignalGrader.Grade(ranked));
    }

    [Fact]
    public void StrongTopWithoutSeparationIsPartial()
    {
        // The top clears the confident floor but the next candidate is inside the clear-gap, so it does not stand clear.
        var ranked = Ranked(0.70, 0.62, 0.60);

        Assert.Equal(SignalState.Partial, SignalGrader.Grade(ranked));
    }

    [Fact]
    public void WeakDistributionIsInsufficient()
    {
        var ranked = Ranked(0.25, 0.22, 0.20);

        Assert.Equal(SignalState.Insufficient, SignalGrader.Grade(ranked));
    }

    [Fact]
    public void EmptyDistributionIsInsufficient()
    {
        Assert.Equal(SignalState.Insufficient, SignalGrader.Grade([]));
        Assert.Empty(SignalGrader.LeadingCluster([]));
    }

    [Fact]
    public void GradeIsReproducibleForFixedScores()
    {
        var ranked = Ranked(0.80, 0.50, 0.45);

        Assert.Equal(SignalGrader.Grade(ranked), SignalGrader.Grade(ranked));
        Assert.Equal(SignalState.Confident, SignalGrader.Grade(ranked));
    }

    private static IReadOnlyList<ScoredCandidate> Ranked(params double[] scores)
    {
        var list = new List<ScoredCandidate>(scores.Length);
        for (var i = 0; i < scores.Length; i++)
        {
            list.Add(new ScoredCandidate(
                $"node:{i}", $"src/File{i}.cs", "type", scores[i], [CandidateSource.FtsBody], ["match"], 10));
        }

        return list;
    }
}
