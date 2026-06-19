using Fuse.Analysis.Dependencies;
using Fuse.Analysis.Search;
using Fuse.Collection.Models;
using Fuse.Collection.Templates;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Languages.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.GoldenOutput.Tests;

public sealed class GoldenFusionTestHost : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly ProjectTemplateRegistry _templateRegistry;

    public GoldenFusionTestHost()
    {
        var services = new ServiceCollection();
        services.AddFuse();
        _serviceProvider = services.BuildServiceProvider();
        _templateRegistry = _serviceProvider.GetRequiredService<ProjectTemplateRegistry>();
    }

    public FusionOrchestrator Orchestrator => _serviceProvider.GetRequiredService<FusionOrchestrator>();

    public async Task<string> FuseSampleShopAsync(
        ReductionOptions? reduction = null,
        EmissionOptions? emission = null,
        FocusOptions? focus = null,
        QueryOptions? query = null,
        int parallelism = 1,
        bool useCache = false)
    {
        var builder = new FusionRequestBuilder(_templateRegistry)
            .WithSourceDirectory(GoldenPaths.SampleShopFixture)
            .WithTemplate(ProjectTemplate.DotNet)
            .WithReductionOptions(reduction ?? new ReductionOptions())
            .WithEmissionOptions(emission ?? new EmissionOptions { IncludeManifest = true, IncludeGitStats = false })
            .WithInMemory(true)
            .WithParallelism(parallelism)
            .WithReductionCacheOptions(useCache, clearCache: true);

        if (focus is not null)
            builder.WithFocusOptions(focus);

        if (query is not null)
            builder.WithQueryOptions(query);

        var request = builder.Build();
        var result = await Orchestrator.FuseAsync(request);
        Assert.NotNull(result.InMemoryContent);
        return result.InMemoryContent;
    }

    public void Dispose() => _serviceProvider.Dispose();
}
