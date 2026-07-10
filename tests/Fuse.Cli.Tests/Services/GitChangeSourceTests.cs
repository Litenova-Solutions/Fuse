using Fuse.Cli.Services;
using Fuse.Fusion.Scoping;
using Fuse.Retrieval;

namespace Fuse.Cli.Tests.Services;

// GitChangeSource adapts IChangeDetector to the retrieval layer's IChangeSource. These verify the base-content
// path (T2): a detector result flows through unchanged, and a detector failure surfaces as ChangeSourceException
// so the retrieval layer sees a single failure type.
public sealed class GitChangeSourceTests
{
    [Fact]
    public async Task GetFileContentAtBase_DelegatesToDetector()
    {
        var source = new GitChangeSource(new StubDetector("public class Foo { }"));
        var content = await source.GetFileContentAtBaseAsync("/repo", "main", "src/Foo.cs", CancellationToken.None);
        Assert.Equal("public class Foo { }", content);
    }

    [Fact]
    public async Task GetFileContentAtBase_AbsentFile_ReturnsNull()
    {
        var source = new GitChangeSource(new StubDetector(null));
        var content = await source.GetFileContentAtBaseAsync("/repo", "main", "src/New.cs", CancellationToken.None);
        Assert.Null(content);
    }

    [Fact]
    public async Task GetFileContentAtBase_DetectorFailure_ThrowsChangeSourceException()
    {
        var source = new GitChangeSource(new ThrowingDetector());
        await Assert.ThrowsAsync<ChangeSourceException>(() =>
            source.GetFileContentAtBaseAsync("/repo", "main", "src/Foo.cs", CancellationToken.None));
    }

    private sealed class StubDetector(string? content) : IChangeDetector
    {
        public Task<IReadOnlyList<string>> GetChangedRelativePathsAsync(
            string sourceDirectory, string since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> GetFileContentAtAsync(
            string sourceDirectory, string reference, string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(content);
    }

    private sealed class ThrowingDetector : IChangeDetector
    {
        public Task<IReadOnlyList<string>> GetChangedRelativePathsAsync(
            string sourceDirectory, string since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> GetFileContentAtAsync(
            string sourceDirectory, string reference, string relativePath, CancellationToken cancellationToken = default) =>
            throw new ChangeDetectionException("git unavailable");
    }
}
