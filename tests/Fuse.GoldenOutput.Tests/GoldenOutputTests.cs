using Fuse.Emission.Models;
using Fuse.Emission.Writers;
using Fuse.Collection.Models;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Reduction.Models;

namespace Fuse.GoldenOutput.Tests;

public sealed class GoldenOutputTests
{
    [Fact]
    public async Task SampleShop_DefaultFusion_MatchesGolden()
    {
        using var host = new GoldenFusionTestHost();
        var output = await host.FuseSampleShopAsync();
        GoldenOutputAssert.AssertMatches("default-fusion", output);
    }

    [Fact]
    public async Task SampleShop_RouteMap_MatchesGolden()
    {
        using var host = new GoldenFusionTestHost();
        var output = await host.FuseSampleShopAsync(
            reduction: new ReductionOptions(includeRouteMap: true),
            emission: new EmissionOptions { IncludeManifest = false, IncludeGitStats = false });
        GoldenOutputAssert.AssertMatches("route-map", output);
    }

    [Fact]
    public async Task SampleShop_ProjectGraph_MatchesGolden()
    {
        using var host = new GoldenFusionTestHost();
        var output = await host.FuseSampleShopAsync(
            reduction: new ReductionOptions(includeProjectGraph: true),
            emission: new EmissionOptions { IncludeManifest = false, IncludeGitStats = false });
        GoldenOutputAssert.AssertMatches("project-graph", output);
    }

    [Fact]
    public void XmlEntryFormatter_SampleEntry_MatchesGolden()
    {
        var formatter = new XmlEntryFormatter();
        var candidate = new FileCandidate(
            Path.Combine(GoldenPaths.SampleShopFixture, "src/SampleShop.Core/Models/CatalogItem.cs"),
            "src/SampleShop.Core/Models/CatalogItem.cs",
            new FileInfo(Path.Combine(GoldenPaths.SampleShopFixture, "src/SampleShop.Core/Models/CatalogItem.cs")));
        var source = new SourceFile(candidate);
        var fused = new FusedContent(
            source,
            "public class CatalogItem { }",
            new SimpleTokenCounter(),
            inclusionChain: ["OrderService.cs", "src/SampleShop.Core/Models/CatalogItem.cs"]);

        var output = formatter.FormatEntry(fused, new EmissionOptions { IncludeProvenance = true, IncludeMetadata = false });
        GoldenOutputAssert.AssertMatches("xml-entry-formatter", output);
    }

    private sealed class SimpleTokenCounter : Fuse.Reduction.Tokenization.ITokenCounter
    {
        public int Count(string content) => content.Length;
    }
}
