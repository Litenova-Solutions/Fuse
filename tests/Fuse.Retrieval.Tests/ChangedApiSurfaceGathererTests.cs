using Fuse.Retrieval;
using Xunit;

namespace Fuse.Retrieval.Tests;

// T2: the gatherer ties the base-ref content (from the change source) to the working-tree content (from an
// injected reader) and computes the delta. Fakes stand in for git and disk so the orchestration - deletions,
// additions, non-C# skipping, and base-read avoidance - is pinned without a repository.
public sealed class ChangedApiSurfaceGathererTests
{
    private sealed class FakeChangeSource(IReadOnlyDictionary<string, string?> baseContent) : IChangeSource
    {
        public int BaseReads { get; private set; }

        public Task<IReadOnlyList<string>> GetChangedFilesAsync(string rootDirectory, string since, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<ChangedFile>> GetDiffsAsync(string rootDirectory, string since, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ChangedFile>>([]);

        public Task<string?> GetFileContentAtBaseAsync(string rootDirectory, string since, string relativePath, CancellationToken cancellationToken)
        {
            BaseReads++;
            return Task.FromResult(baseContent.GetValueOrDefault(relativePath));
        }
    }

    private static Func<string, CancellationToken, Task<string?>> CurrentReader(IReadOnlyDictionary<string, string?> current) =>
        (path, _) => Task.FromResult(current.GetValueOrDefault(path));

    [Fact]
    public async Task A_modified_file_reports_the_member_delta()
    {
        var source = new FakeChangeSource(new Dictionary<string, string?>
        {
            ["src/Api.cs"] = "public class Api { public void Foo() { } public void Bar() { } }",
        });
        var reader = CurrentReader(new Dictionary<string, string?>
        {
            ["src/Api.cs"] = "public class Api { public void Foo() { } }",
        });

        var delta = await ChangedApiSurfaceGatherer.GatherAsync(
            source, "/repo", "main", ["src/Api.cs"], reader, CancellationToken.None);

        Assert.True(delta.HasBreaking);
        Assert.Contains(delta.Changes, c => c.Kind == ApiChangeKind.Removed && c.Symbol.Contains("Bar"));
    }

    [Fact]
    public async Task A_deleted_file_has_no_current_content_and_reports_removals()
    {
        var source = new FakeChangeSource(new Dictionary<string, string?>
        {
            ["src/Gone.cs"] = "public class Gone { public void WasHere() { } }",
        });
        var reader = CurrentReader(new Dictionary<string, string?>()); // absent on disk => null

        var delta = await ChangedApiSurfaceGatherer.GatherAsync(
            source, "/repo", "main", ["src/Gone.cs"], reader, CancellationToken.None);

        Assert.True(delta.HasBreaking);
        Assert.Contains(delta.Changes, c => c.Kind == ApiChangeKind.Removed);
    }

    [Fact]
    public async Task A_non_csharp_file_is_skipped_without_a_base_read()
    {
        var source = new FakeChangeSource(new Dictionary<string, string?>());
        var reader = CurrentReader(new Dictionary<string, string?> { ["config.json"] = "{}" });

        var delta = await ChangedApiSurfaceGatherer.GatherAsync(
            source, "/repo", "main", ["config.json"], reader, CancellationToken.None);

        Assert.Empty(delta.Changes);
        Assert.Equal(0, source.BaseReads); // the git-show subprocess is not spent on a non-C# path
    }

    [Fact]
    public async Task An_unchanged_public_surface_yields_no_delta()
    {
        const string same = "public class Api { public void Foo() { } }";
        var source = new FakeChangeSource(new Dictionary<string, string?> { ["src/Api.cs"] = same });
        var reader = CurrentReader(new Dictionary<string, string?> { ["src/Api.cs"] = same });

        var delta = await ChangedApiSurfaceGatherer.GatherAsync(
            source, "/repo", "main", ["src/Api.cs"], reader, CancellationToken.None);

        Assert.Empty(delta.Changes);
    }
}
