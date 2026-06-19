using Fuse.Languages.Abstractions;
using Fuse.Languages.Abstractions.Reducers;
using Fuse.Languages.CSharp.Reducers;

namespace Fuse.Languages.CSharp.Tests;

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
            new Fuse.Formats.Reducers.HtmlReducer(),
        ]);

        Assert.NotNull(registry.TryResolve(".html"));
        Assert.NotNull(registry.TryResolve(".htm"));
    }
}
