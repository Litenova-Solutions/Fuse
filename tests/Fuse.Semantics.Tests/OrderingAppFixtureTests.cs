using Microsoft.CodeAnalysis;
using Xunit;
using Xunit.Abstractions;

namespace Fuse.Semantics.Tests;

// P4.1: the shared semantic fixture compiles cleanly in-memory and declares every type the Phase 4
// analyzers assert against.
public sealed class OrderingAppFixtureTests
{
    private readonly ITestOutputHelper _output;

    public OrderingAppFixtureTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void FixtureCompilesWithoutErrors()
    {
        var project = OrderingAppFixture.Load();

        var errors = project.Compilation.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToList();
        foreach (var error in errors)
            _output.WriteLine(error.ToString());

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("IOrderService")]
    [InlineData("OrderService")]
    [InlineData("OrderOptions")]
    [InlineData("CreateOrderCommand")]
    [InlineData("CreateOrderHandler")]
    [InlineData("OrdersController")]
    [InlineData("OrderServiceTests")]
    public void FixtureDeclaresExpectedType(string typeName)
    {
        var project = OrderingAppFixture.Load();

        Assert.NotEmpty(project.Compilation.GetSymbolsWithName(typeName, SymbolFilter.Type));
    }

    [Fact]
    public void OrderServiceImplementsInterfaceAndHandlerImplementsRequestHandler()
    {
        var project = OrderingAppFixture.Load();

        var orderService = (INamedTypeSymbol)project.Compilation.GetSymbolsWithName("OrderService", SymbolFilter.Type).Single();
        Assert.Contains(orderService.AllInterfaces, i => i.Name == "IOrderService");

        var handler = (INamedTypeSymbol)project.Compilation.GetSymbolsWithName("CreateOrderHandler", SymbolFilter.Type).Single();
        Assert.Contains(handler.AllInterfaces, i => i.Name == "IRequestHandler");
    }
}
