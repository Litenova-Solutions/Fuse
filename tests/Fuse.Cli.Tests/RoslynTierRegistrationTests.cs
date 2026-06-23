using Fuse.Cli.Extensions;
using Fuse.Fusion.Extensions;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Skeleton;
using Fuse.Plugins.Languages.CSharp.Roslyn;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Cli.Tests;

public sealed class RoslynTierRegistrationTests
{
    [Fact]
    public void AddFuse_RoslynSkeletonExtractorResolvesForCSharp()
    {
        var services = new ServiceCollection();
        services.AddFuse();
        var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<CapabilityRegistry<ISkeletonExtractor>>();
        var resolved = registry.TryResolve(".cs");

        Assert.IsType<RoslynSkeletonExtractor>(resolved);
    }

    [Fact]
    public void AddFuseCore_SkeletonExtractorDoesNotResolveForCSharp()
    {
        var services = new ServiceCollection();
        services.AddFuseCore();
        var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<CapabilityRegistry<ISkeletonExtractor>>();

        Assert.Null(registry.TryResolve(".cs"));
    }
}
