using System.Text;
using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Plugins.Abstractions.Outline;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

// Item 16: a file too large even reduced is replaced with its structural outline (type and member names), so
// it keeps presence and navigation at a fraction of the tokens. Opt-in via SketchHugeFiles.
public sealed class FusionOrchestratorSketchTests : IDisposable
{
    private readonly string _sourceDirectory;

    public FusionOrchestratorSketchTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-sketch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sourceDirectory);

        // A file far over the sketch token threshold: many members plus a distinctive body marker.
        var builder = new StringBuilder("public class Huge\n{\n");
        for (var i = 0; i < 1200; i++)
            builder.Append($"    public int Method{i}() => {i};\n");
        builder.Append("    public string Marker() => \"unique-huge-body-marker\";\n}\n");
        File.WriteAllText(Path.Combine(_sourceDirectory, "Huge.cs"), builder.ToString());
    }

    [Fact]
    public void Build_RendersOutlineWithoutBodies()
    {
        var outline = new List<OutlineSymbol>
        {
            new("class", "OrderService", ["PlaceOrder", "Cancel"]),
            new("interface", "IRepo", []),
        };

        var sketch = FileSketchBuilder.Build("Services/OrderService.cs", outline);

        Assert.Contains("fuse:sketch Services/OrderService.cs", sketch);
        Assert.Contains("class OrderService: PlaceOrder, Cancel", sketch);
        Assert.Contains("interface IRepo", sketch);
    }

    [Fact]
    public void Build_EmptyOutline_IsEmpty()
    {
        Assert.Equal(string.Empty, FileSketchBuilder.Build("X.cs", []));
    }

    [Fact]
    public void Build_CapsMembersPerType()
    {
        var members = Enumerable.Range(0, 60).Select(i => $"M{i}").ToList();
        var sketch = FileSketchBuilder.Build("Big.cs", [new("class", "Big", members)]);

        Assert.Contains("... (20 more)", sketch); // 60 members, cap 40
    }

    [Fact]
    public async Task FuseAsync_SketchOn_ReplacesHugeFileWithOutline()
    {
        var on = await EmittedAsync(sketch: true);

        // The type and its early member names survive (navigation), the member cap is noted, but no method
        // body does.
        Assert.Contains("class Huge", on);
        Assert.Contains("Method0", on);
        Assert.Contains("more)", on); // 1201 members, capped
        Assert.DoesNotContain("unique-huge-body-marker", on);
        Assert.Contains("fuse:sketch", on);
    }

    [Fact]
    public async Task FuseAsync_SketchOff_KeepsBody()
    {
        var off = await EmittedAsync(sketch: false);

        Assert.Contains("unique-huge-body-marker", off);
        Assert.DoesNotContain("fuse:sketch", off);
    }

    private async Task<string> EmittedAsync(bool sketch)
    {
        using var provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<FusionOrchestrator>();

        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(level: ReductionLevel.None),
            new EmissionOptions { IncludeManifest = false },
            inMemory: true,
            focus: new FocusOptions("Huge", 1),
            experimental: new ExperimentalOptions { SketchHugeFiles = sketch });

        var result = await orchestrator.FuseAsync(request);
        Assert.NotNull(result.InMemoryContent);
        return result.InMemoryContent!;
    }

    public void Dispose()
    {
        if (Directory.Exists(_sourceDirectory))
            Directory.Delete(_sourceDirectory, recursive: true);
    }
}
