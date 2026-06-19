using Fuse.Languages.CSharp.Markers;

namespace Fuse.Languages.CSharp.Tests.Markers;

public sealed class CSharpSemanticMarkerGeneratorTests
{
    private readonly CSharpSemanticMarkerGenerator _generator = new();

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
}
