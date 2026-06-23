using Fuse.Fusion.Extensions;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Skeleton;
using Fuse.Plugins.Languages.CSharp.Roslyn;
using Fuse.Plugins.Languages.CSharp.Roslyn.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Cli.Tests;

public sealed class RoslynTierRegistrationTests
{
    [Fact]
    public void AddFuseWithRoslyn_RoslynSkeletonExtractorResolvesForCSharp()
    {
        var services = new ServiceCollection();
        services.AddFuse();
        services.AddCSharpRoslyn();
        var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<CapabilityRegistry<ISkeletonExtractor>>();
        var resolved = registry.TryResolve(".cs");

        Assert.IsType<RoslynSkeletonExtractor>(resolved);
    }
}
