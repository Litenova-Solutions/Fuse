using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Markers;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Plugins.Abstractions.Skeleton;
using Fuse.Plugins.Languages.CSharp.Roslyn;
using Fuse.Reduction;
using Fuse.Reduction.Security;
using Fuse.Reduction.Tokenization;

namespace Fuse.Fusion.Tests;

// A1 / tiered-emission mechanism: ContentReductionPipeline can reduce different files at different tiers in one
// pass via the per-file level selector, which is the single-pass, redaction-correct path tiered emission uses
// instead of re-reading source in the orchestrator.
public sealed class PerFileReductionLevelTests
{
    private const string SeedSource = """
        public class SeedService
        {
            public int Compute() { return 1 + seed_body_marker; }
        }
        """;

    private const string NeighbourSource = """
        public class NeighbourService
        {
            public int Helper() { return 2 + neighbour_body_marker; }
        }
        """;

    [Fact]
    public async Task ReduceAsync_PerFileLevel_SkeletonizesOneFileAndKeepsAnotherFull()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-perfile-level", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var seed = WriteSource(root, "SeedService.cs", SeedSource);
            var neighbour = WriteSource(root, "NeighbourService.cs", NeighbourSource);
            var pipeline = CreatePipeline();
            var provider = new RealContentProvider();

            // Seed stays at None (full body); neighbour is skeletonized (signatures only).
            ReductionLevel Level(SourceFile f) =>
                f.NormalizedRelativePath.Contains("Neighbour") ? ReductionLevel.Skeleton : ReductionLevel.None;

            var reduced = await pipeline.ReduceAsync(
                [seed, neighbour],
                new ReductionOptions(level: ReductionLevel.None),
                provider,
                parallelism: 1,
                reductionCache: null,
                tokenCounterOverride: null,
                perFileLevel: Level);

            var seedOut = reduced.Single(c => c.NormalizedPath.Contains("Seed"));
            var neighbourOut = reduced.Single(c => c.NormalizedPath.Contains("Neighbour"));

            // The seed keeps its body; the neighbour is reduced to signatures, dropping its body.
            Assert.Contains("seed_body_marker", seedOut.Content);
            Assert.Contains("NeighbourService", neighbourOut.Content);
            Assert.DoesNotContain("neighbour_body_marker", neighbourOut.Content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SourceFile WriteSource(string root, string name, string content)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, content);
        return new SourceFile(new FileCandidate(path, name, new FileInfo(path)));
    }

    private static ContentReductionPipeline CreatePipeline() =>
        new(
            new CapabilityRegistry<IContentReducer>([]),
            new CapabilityRegistry<ISkeletonExtractor>([new RoslynSkeletonExtractor()]),
            new CapabilityRegistry<ISemanticMarkerGenerator>([]),
            new SimpleTokenCounter(),
            new DefaultSecretRedactor());

    private sealed class RealContentProvider : ISourceContentProvider
    {
        public Task<string> GetContentAsync(SourceFile file, CancellationToken cancellationToken = default) =>
            Task.FromResult(File.ReadAllText(file.FullPath));

        public void Clear()
        {
        }
    }

    private sealed class SimpleTokenCounter : ITokenCounter
    {
        public int Count(string content) => content.Length;
    }
}
