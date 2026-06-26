using Fuse.Semantics.Analyzers;
using Xunit;

namespace Fuse.Semantics.Tests;

// P4.3: DI registration detection and the di_resolves_to edge over the OrderingApp fixture.
public sealed class DiRegistrationAnalyzerTests
{
    [Fact]
    public void EmitsResolvesToEdgeForRegisteredService()
    {
        var result = Analyze();

        Assert.Contains(result.Edges, e =>
            e.EdgeType == "di_resolves_to"
            && e.FromNodeId == "type:OrderingApp.Ordering.IOrderService"
            && e.ToNodeId == "type:OrderingApp.Ordering.OrderService"
            && e.Weight == 0.95);
    }

    [Fact]
    public void RecordsRegistrationWithLifetimeAndSymbols()
    {
        var result = Analyze();

        var registration = Assert.Single(result.DiRegistrations, r => r.ServiceName == "OrderingApp.Ordering.IOrderService");
        Assert.Equal("Scoped", registration.Lifetime);
        Assert.Equal("generic2", registration.RegistrationKind);
        Assert.Equal("OrderingApp.Ordering.OrderService", registration.ImplementationName);
        Assert.NotNull(registration.ServiceSymbolId);
        Assert.NotNull(registration.ImplementationSymbolId);
    }

    [Theory]
    [InlineData("services.AddScoped<App.IFoo>();", "generic1", "App.IFoo", "App.IFoo")]
    [InlineData("services.AddSingleton(typeof(App.IFoo), typeof(App.Foo));", "typeof", "App.IFoo", "App.Foo")]
    [InlineData("services.AddTransient<App.IFoo>(sp => new App.Foo());", "factory", "App.IFoo", null)]
    public void HandlesRegistrationShapes(string call, string expectedKind, string expectedService, string? expectedImpl)
    {
        var source = $$"""
            using Microsoft.Extensions.DependencyInjection;
            namespace App
            {
                public interface IFoo { }
                public sealed class Foo : IFoo { }
                public static class Reg
                {
                    public static void Configure(IServiceCollection services) { {{call}} }
                }
            }
            """;
        var result = AnalyzeSource(source);

        var registration = Assert.Single(result.DiRegistrations);
        Assert.Equal(expectedKind, registration.RegistrationKind);
        Assert.Equal(expectedService, registration.ServiceName);
        Assert.Equal(expectedImpl, registration.ImplementationName);
    }

    private static SemanticAnalyzerResult Analyze()
    {
        var project = OrderingAppFixture.Load();
        return new DiRegistrationAnalyzer().Analyze(new SemanticAnalysisContext(project, OrderingAppFixture.RootDirectory), CancellationToken.None);
    }

    private static SemanticAnalyzerResult AnalyzeSource(string source)
    {
        var project = InlineCompilation.Load(source);
        return new DiRegistrationAnalyzer().Analyze(new SemanticAnalysisContext(project, "/repo"), CancellationToken.None);
    }
}
