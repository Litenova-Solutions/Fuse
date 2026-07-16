using Microsoft.Build.Locator;
using Xunit;

namespace Fuse.Semantics.Tests;

public sealed class MsBuildLocatorRegistrationTests
{
    [Fact]
    [Trait("Category", "RequiresSdk")]
    public async Task Concurrent_registration_is_serialized_process_wide()
    {
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(MsBuildLocatorRegistration.EnsureRegistered))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.True(MSBuildLocator.IsRegistered);
    }
}
