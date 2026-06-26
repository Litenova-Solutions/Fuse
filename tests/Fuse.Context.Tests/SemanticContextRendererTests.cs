using Fuse.Collection.FileSystem;
using Fuse.Context;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Markers;
using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Plugins.Abstractions.Skeleton;
using Fuse.Plugins.Languages.CSharp.Roslyn;
using Fuse.Reduction;
using Fuse.Reduction.Security;
using Fuse.Reduction.Tokenization;
using Fuse.Retrieval;
using Xunit;

namespace Fuse.Context.Tests;

// P7.1: tiered rendering - full source keeps bodies, skeleton drops them, omitted is excluded.
public sealed class SemanticContextRendererTests : IDisposable
{
    private const string Source = """
        namespace App;

        public class OrderService
        {
            public int Create(int quantity)
            {
                var doubled = quantity * 2;
                return doubled;
            }
        }
        """;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fuse-render-tests", Guid.NewGuid().ToString("N"));

    public SemanticContextRendererTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "OrderService.cs"), Source);
        File.WriteAllText(Path.Combine(_root, "src", "Other.cs"), "namespace App; public class Other { }");
    }

    [Fact]
    public async Task FullSourceKeepsBodyAndSkeletonDropsIt()
    {
        var plan = new ContextPlan("context",
        [
            Item("src/OrderService.cs", RenderTier.FullSource),
            Item("src/Other.cs", RenderTier.Skeleton),
        ], [], 0, []);

        var rendered = await CreateRenderer().RenderAsync(plan, _root, CancellationToken.None);

        var full = rendered.Files.Single(f => f.Path == "src/OrderService.cs");
        Assert.Contains("var doubled = quantity * 2", full.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SkeletonTierDropsMethodBody()
    {
        var plan = new ContextPlan("context",
        [
            Item("src/OrderService.cs", RenderTier.Skeleton),
        ], [], 0, []);

        var rendered = await CreateRenderer().RenderAsync(plan, _root, CancellationToken.None);

        var skeleton = rendered.Files.Single(f => f.Path == "src/OrderService.cs");
        Assert.Contains("Create", skeleton.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("var doubled = quantity * 2", skeleton.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OmittedTierIsExcluded()
    {
        var plan = new ContextPlan("context",
        [
            Item("src/OrderService.cs", RenderTier.FullSource),
            Item("src/Other.cs", RenderTier.Omitted),
        ], [], 0, []);

        var rendered = await CreateRenderer().RenderAsync(plan, _root, CancellationToken.None);

        Assert.DoesNotContain(rendered.Files, f => f.Path == "src/Other.cs");
        Assert.True(rendered.TotalTokens > 0);
    }

    private static ContextPlanItem Item(string path, RenderTier tier) =>
        new(path, null, "dependency", tier, 0.5, 0, false, [], []);

    private static SemanticContextRenderer CreateRenderer()
    {
        var pipeline = new ContentReductionPipeline(
            new CapabilityRegistry<IContentReducer>([]),
            new CapabilityRegistry<ISkeletonExtractor>([new RoslynSkeletonExtractor()]),
            new CapabilityRegistry<ISemanticMarkerGenerator>([]),
            new LengthTokenCounter(),
            new DefaultSecretRedactor());
        return new SemanticContextRenderer(pipeline, new SourceContentProvider(new PhysicalFileSystem()));
    }

    private sealed class LengthTokenCounter : ITokenCounter
    {
        public int Count(string content) => (content.Length + 3) / 4;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp test artifacts.
        }
    }
}
