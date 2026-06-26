using Fuse.Retrieval;
using Xunit;

namespace Fuse.Retrieval.Tests;

// P6.1: ChangedSince resolves through an IChangeSource into must-keep diff candidates.
public sealed class DiffChangeSourceTests
{
    [Fact]
    public async Task ChangedSinceProducesDiffCandidates()
    {
        var changeSource = new FakeChangeSource(["src/OrderService.cs", "src/OrdersController.cs"]);
        var generator = new DiffCandidateGenerator(changeSource);

        var candidates = await generator.GenerateAsync(
            new LocalizationRequest(".", ChangedSince: "origin/main"), CancellationToken.None);

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, c => Assert.Equal(CandidateSource.DiffChangedFile, c.Source));
        Assert.All(candidates, c => Assert.Equal(1.00, c.BaseScore));
        Assert.Contains(candidates, c => c.FilePath == "src/OrderService.cs");
    }

    [Fact]
    public async Task SelectedPathsAndChangedSinceCombineWithoutDuplicates()
    {
        var changeSource = new FakeChangeSource(["src/OrderService.cs"]);
        var generator = new DiffCandidateGenerator(changeSource);

        var candidates = await generator.GenerateAsync(
            new LocalizationRequest(".", ChangedSince: "HEAD~1", SelectedPaths: ["src/OrderService.cs", "src/Extra.cs"]),
            CancellationToken.None);

        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, c => c.FilePath == "src/Extra.cs");
    }

    [Fact]
    public async Task ChangeSourceFailureIsSwallowed()
    {
        var generator = new DiffCandidateGenerator(new ThrowingChangeSource());

        var candidates = await generator.GenerateAsync(
            new LocalizationRequest(".", ChangedSince: "origin/main"), CancellationToken.None);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task NoChangeSourceMeansNoDiffCandidatesFromChangedSince()
    {
        var generator = new DiffCandidateGenerator(changeSource: null);

        var candidates = await generator.GenerateAsync(
            new LocalizationRequest(".", ChangedSince: "origin/main"), CancellationToken.None);

        Assert.Empty(candidates);
    }

    private sealed class FakeChangeSource(IReadOnlyList<string> changed) : IChangeSource
    {
        public Task<IReadOnlyList<string>> GetChangedFilesAsync(string rootDirectory, string since, CancellationToken cancellationToken) =>
            Task.FromResult(changed);

        public Task<IReadOnlyList<ChangedFile>> GetDiffsAsync(string rootDirectory, string since, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ChangedFile>>(changed.Select(p => new ChangedFile(p, 1, 0, string.Empty)).ToList());
    }

    private sealed class ThrowingChangeSource : IChangeSource
    {
        public Task<IReadOnlyList<string>> GetChangedFilesAsync(string rootDirectory, string since, CancellationToken cancellationToken) =>
            throw new ChangeSourceException("git not available");

        public Task<IReadOnlyList<ChangedFile>> GetDiffsAsync(string rootDirectory, string since, CancellationToken cancellationToken) =>
            throw new ChangeSourceException("git not available");
    }
}
