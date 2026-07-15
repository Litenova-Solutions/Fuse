using Fuse.Indexing;
using Fuse.Retrieval;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Retrieval.Tests;

// A6: the git co-change prior boosts a candidate that historically changes alongside a strong hit, recovering
// the sibling files of a multi-file change, bounded so it cannot promote a near-zero-score file on co-change
// alone, and a no-op when no co-change data was mined.
public sealed class GitCoChangePriorTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-cochange-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CoChangingSiblingOfAStrongHitIsBoosted()
    {
        // The strong hit is OrderService; OrderRepository co-changes with it strongly (Jaccard 0.8). A weaker
        // candidate that does not co-change keeps its score, so the sibling overtakes it.
        await _store.UpsertCoChangesAsync(
            [new CoChangeRecord("src/OrderRepository.cs", "src/OrderService.cs", 5, 1.5, 0.8, null)],
            CancellationToken.None);
        var prior = new GitCoChangePrior(_store);

        var candidates = new List<ScoredCandidate>
        {
            new(string.Empty, "src/OrderService.cs", "file", 0.90, [CandidateSource.FtsSymbol], ["strong"], 10),
            new(string.Empty, "src/Unrelated.cs", "file", 0.50, [CandidateSource.FtsBody], ["weak"], 10),
            new(string.Empty, "src/OrderRepository.cs", "file", 0.46, [CandidateSource.FtsBody], ["sibling"], 10)
        };

        var ranked = await prior.ApplyAsync(candidates, CancellationToken.None);

        var repository = ranked.Single(c => c.FilePath == "src/OrderRepository.cs");
        var unrelated = ranked.Single(c => c.FilePath == "src/Unrelated.cs");
        Assert.True(repository.Score > 0.46, "the co-changing sibling should be boosted above its base score");
        Assert.True(repository.Score > unrelated.Score, "the boosted sibling should overtake the non-co-changing candidate");
    }

    [Fact]
    public async Task PriorIsBoundedAndCannotPromoteANearZeroCandidate()
    {
        await _store.UpsertCoChangesAsync(
            [new CoChangeRecord("src/OrderService.cs", "src/Weak.cs", 5, 1.5, 1.0, null)],
            CancellationToken.None);
        var prior = new GitCoChangePrior(_store);

        // Weak co-changes maximally (Jaccard 1.0) with the strong seed but scores near zero; the capped +15
        // percent multiplier cannot lift it over a strong, unrelated candidate.
        var candidates = new List<ScoredCandidate>
        {
            new(string.Empty, "src/OrderService.cs", "file", 0.95, [CandidateSource.FtsSymbol], ["seed"], 10),
            new(string.Empty, "src/Strong.cs", "file", 0.80, [CandidateSource.FtsSymbol], ["strong"], 10),
            new(string.Empty, "src/Weak.cs", "file", 0.05, [CandidateSource.FtsBody], ["weak"], 10)
        };

        var ranked = await prior.ApplyAsync(candidates, CancellationToken.None);

        Assert.NotEqual("src/Weak.cs", ranked[0].FilePath);
        Assert.True(ranked.Single(c => c.FilePath == "src/Weak.cs").Score < 0.80);
    }

    [Fact]
    public async Task PriorIsANoOpWithNoCoChangeData()
    {
        var prior = new GitCoChangePrior(_store);
        var candidates = new List<ScoredCandidate>
        {
            new(string.Empty, "src/A.cs", "file", 0.60, [CandidateSource.FtsSymbol], ["a"], 10),
            new(string.Empty, "src/B.cs", "file", 0.40, [CandidateSource.FtsBody], ["b"], 10)
        };

        var ranked = await prior.ApplyAsync(candidates, CancellationToken.None);

        Assert.Equal(candidates[0].Score, ranked[0].Score);
        Assert.Equal(candidates[1].Score, ranked[1].Score);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        var directory = Path.GetDirectoryName(_databasePath);
        try
        {
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
