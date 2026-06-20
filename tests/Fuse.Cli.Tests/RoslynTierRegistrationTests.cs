using Fuse.Fusion.Extensions;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Skeleton;
using Fuse.Plugins.Languages.CSharp.Roslyn;
using Fuse.Plugins.Languages.CSharp.Roslyn.Extensions;
using Fuse.Plugins.Languages.CSharp.Skeleton;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Cli.Tests;

public sealed class RoslynTierRegistrationTests
{
    [Fact]
    public void WithoutOptIn_RegexSkeletonExtractorResolvesForCSharp()
    {
        var provider = new ServiceCollection().AddFuse().BuildServiceProvider();

        var registry = provider.GetRequiredService<CapabilityRegistry<ISkeletonExtractor>>();
        var resolved = registry.TryResolve(".cs");

        Assert.IsType<CSharpSkeletonExtractor>(resolved);
    }

    [Fact]
    public void WithOptIn_RoslynSkeletonExtractorWinsForCSharp()
    {
        var services = new ServiceCollection();
        services.AddFuse();
        services.AddCSharpRoslyn();
        var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<CapabilityRegistry<ISkeletonExtractor>>();
        var resolved = registry.TryResolve(".cs");

        // Last registration wins, so the opt-in Roslyn tier takes over .cs.
        Assert.IsType<RoslynSkeletonExtractor>(resolved);
    }

    [Theory]
    [InlineData(new[] { "dotnet", "--semantic" }, true)]
    [InlineData(new[] { "dotnet", "--directory", "." }, false)]
    public void SemanticModeDetector_DetectsFlag(string[] args, bool expected)
    {
        Assert.Equal(expected, SemanticModeDetector.IsRequested(args));
    }
}
