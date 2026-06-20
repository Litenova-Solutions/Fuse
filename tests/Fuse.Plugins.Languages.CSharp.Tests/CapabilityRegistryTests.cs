using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Plugins.Languages.CSharp.Reducers;

namespace Fuse.Plugins.Languages.CSharp.Tests;

public sealed class CapabilityRegistryTests
{
    [Fact]
    public void TryResolve_KnownExtension_ReturnsCapability()
    {
        var registry = new CapabilityRegistry<IContentReducer>([new CSharpReducer()]);
        Assert.NotNull(registry.TryResolve(".cs"));
    }

    [Fact]
    public void TryResolve_UnknownExtension_ReturnsNull()
    {
        var registry = new CapabilityRegistry<IContentReducer>([new CSharpReducer()]);
        Assert.Null(registry.TryResolve(".py"));
    }

    [Fact]
    public void TryResolve_LastRegistrationWins()
    {
        var first = new CSharpReducer();
        var second = new CSharpReducer();
        var registry = new CapabilityRegistry<IContentReducer>([first, second]);
        Assert.Same(second, registry.TryResolve(".cs"));
    }

    [Fact]
    public void TryResolve_MultiExtensionCapability()
    {
        var registry = new CapabilityRegistry<IContentReducer>([
            new Fuse.Plugins.Formats.Web.Reducers.HtmlReducer(),
        ]);

        Assert.NotNull(registry.TryResolve(".html"));
        Assert.NotNull(registry.TryResolve(".htm"));
    }
}
