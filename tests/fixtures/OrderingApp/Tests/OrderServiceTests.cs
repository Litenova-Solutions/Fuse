using OrderingApp.Ordering;
using Xunit;

namespace OrderingApp.Tests;

// Named after its subject (OrderService) so the test analyzer links the test to it (the tests edge).
public sealed class OrderServiceTests
{
    [Fact]
    public void CreateClampsToMaxItems()
    {
        // Subject referenced directly so the test-to-subject link is also visible by reference.
        _ = nameof(OrderService);
    }
}
