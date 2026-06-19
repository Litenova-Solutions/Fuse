using Fuse.Analysis.Dependencies;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;
using Fuse.Collection.Templates;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Languages.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.GoldenOutput.Tests;

public sealed class SourceContentReadOnceTests
{
    [Fact]
    public async Task FuseAsync_FocusMode_ReadsEachCollectedFileAtMostOnce()
    {
        var services = new ServiceCollection();
        services.AddFuse();
        services.AddSingleton<ISourceContentProvider>(sp =>
            new CountingSourceContentProvider(sp.GetRequiredService<IFileSystem>()));
        await using var provider = services.BuildServiceProvider();

        var counter = (CountingSourceContentProvider)provider.GetRequiredService<ISourceContentProvider>();
        var orchestrator = provider.GetRequiredService<FusionOrchestrator>();
        var templateRegistry = provider.GetRequiredService<ProjectTemplateRegistry>();

        var request = new FusionRequestBuilder(templateRegistry)
            .WithSourceDirectory(GoldenPaths.SampleShopFixture)
            .WithTemplate(ProjectTemplate.DotNet)
            .WithReductionOptions(new ReductionOptions())
            .WithEmissionOptions(new EmissionOptions { IncludeManifest = false, IncludeGitStats = false })
            .WithInMemory(true)
            .WithFocusOptions(new FocusOptions("OrderService", Depth: 2))
            .WithReductionCacheOptions(useCache: false, clearCache: true)
            .Build();

        var collection = await provider.GetRequiredService<Fuse.Collection.FileCollectionPipeline>()
            .CollectAsync(request.Collection);

        await orchestrator.FuseAsync(request);

        Assert.InRange(counter.ReadCount, 1, collection.Files.Count);
    }
}
