using Fuse.Plugins.Abstractions.Options;
using Fuse.Plugins.Languages.CSharp.Reducers;
using Fuse.Plugins.Languages.CSharp.Roslyn.Markers;

namespace Fuse.Plugins.Languages.CSharp.Roslyn.Tests;

public sealed class RoslynSemanticMarkerGeneratorTests
{
    private readonly RoslynSemanticMarkerGenerator _generator = new();
    private readonly RoslynSkeletonExtractor _skeletonExtractor = new();
    private readonly CSharpReducer _reducer = new();

    [Fact]
    public void GenerateMarkers_ExtractsConstructorDependencies()
    {
        const string input = """
            public class OrderService
            {
                public OrderService(IPaymentGateway gateway, ILogger logger) { }
            }
            """;

        var markers = _generator.GenerateMarkers(input);

        Assert.Single(markers);
        Assert.Equal("OrderService", markers[0].TypeName);
        Assert.Contains("IPaymentGateway", markers[0].ConstructorParameterTypes);
        Assert.Contains("ILogger", markers[0].ConstructorParameterTypes);
    }

    [Fact]
    public void GenerateMarkers_ExtractsImplementedInterfaces()
    {
        const string input = """
            public class PaymentGateway : IPaymentGateway, IDisposable
            {
                public void Charge() { }
            }
            """;

        var markers = _generator.GenerateMarkers(input);

        Assert.Single(markers);
        Assert.Contains("IPaymentGateway", markers[0].Implements);
        Assert.Contains("IDisposable", markers[0].Implements);
    }

    [Fact]
    public void GenerateMarkers_DropsBaseClassFromImplements()
    {
        const string input = """
            public class WidgetService : BaseService, IWidgetService
            {
                public WidgetService() { }
            }
            """;

        var markers = _generator.GenerateMarkers(input);

        Assert.Single(markers);
        Assert.DoesNotContain("BaseService", markers[0].Implements);
        Assert.Contains("IWidgetService", markers[0].Implements);
    }

    [Fact]
    public void GenerateMarkers_InterfaceTypedProperty_AddsDependsOn()
    {
        const string input = """
            public class OrderService
            {
                public OrderService() { }
                public IPaymentGateway Gateway { get; set; }
            }
            """;

        var markers = _generator.GenerateMarkers(input);

        Assert.Single(markers);
        Assert.Contains("IPaymentGateway", markers[0].DependsOn);
    }

    [Fact]
    public void GenerateMarkers_OnSkeletonOutput_StillExtractsConstructorDependencies()
    {
        const string input = """
            public class OrderService
            {
                public OrderService(IPaymentGateway gateway, ILogger logger)
                {
                    gateway.Charge();
                }
            }
            """;

        var skeleton = _skeletonExtractor.ExtractSkeleton(input);
        var markers = _generator.GenerateMarkers(skeleton);

        Assert.Single(markers);
        Assert.Equal("OrderService", markers[0].TypeName);
        Assert.Contains("IPaymentGateway", markers[0].ConstructorParameterTypes);
        Assert.Contains("ILogger", markers[0].ConstructorParameterTypes);
    }

    [Fact]
    public void GenerateMarkers_OnAggressiveReducedOutput_StillExtractsType()
    {
        const string input = """
            public class Widget
            {
                public Widget(IRepository repo) { }
                public int Id { get; set; }
            }
            """;

        var reduced = _reducer.Reduce(input, new ReductionOptions(level: ReductionLevel.Aggressive));
        var markers = _generator.GenerateMarkers(reduced);

        Assert.Single(markers);
        Assert.Equal("Widget", markers[0].TypeName);
        Assert.Contains("IRepository", markers[0].ConstructorParameterTypes);
    }

    [Fact]
    public void GenerateMarkers_MalformedSource_DoesNotThrow()
    {
        const string input = """
            public class Broken
            {
                public Broken(IThing thing)
                // missing closing braces
            """;

        var markers = _generator.GenerateMarkers(input);

        Assert.NotEmpty(markers);
        Assert.Equal("Broken", markers[0].TypeName);
    }

    [Fact]
    public void GenerateMarkers_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(_generator.GenerateMarkers(string.Empty));
        Assert.Empty(_generator.GenerateMarkers("   "));
    }
}
