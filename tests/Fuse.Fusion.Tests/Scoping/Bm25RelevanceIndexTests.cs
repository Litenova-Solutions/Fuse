using Fuse.Fusion.Scoping;

namespace Fuse.Fusion.Tests.Scoping;

public sealed class Bm25RelevanceIndexTests
{
    [Fact]
    public void Rank_QueryMatchesRelevantClusterFirst()
    {
        var index = new Bm25RelevanceIndex();
        index.Index(new Dictionary<string, string>
        {
            ["Billing/InvoiceService.cs"] = "public class InvoiceService { public void ProcessPayment() {} }",
            ["Billing/PaymentGateway.cs"] = "public class PaymentGateway { public void ChargeCard() {} }",
            ["Catalog/ProductService.cs"] = "public class ProductService { public void ListProducts() {} }",
        });

        var ranked = index.Rank("payment invoice billing", topN: 2);

        Assert.Equal(2, ranked.Count);
        Assert.Contains("Billing/InvoiceService.cs", ranked);
        Assert.Contains("Billing/PaymentGateway.cs", ranked);
        Assert.DoesNotContain("Catalog/ProductService.cs", ranked);
    }

    [Fact]
    public void Rank_SplitsIdentifiersIntoSubterms()
    {
        var index = new Bm25RelevanceIndex();
        index.Index(new Dictionary<string, string>
        {
            ["Services/OrderFulfillment.cs"] = "public class OrderFulfillmentService {}",
            ["Other/Random.cs"] = "public class WidgetFactory {}",
        });

        var ranked = index.Rank("order fulfillment", topN: 1);

        Assert.Single(ranked);
        Assert.Equal("Services/OrderFulfillment.cs", ranked[0]);
    }

    [Fact]
    public void Rank_EmptyQuery_ReturnsEmpty()
    {
        var index = new Bm25RelevanceIndex();
        index.Index(new Dictionary<string, string> { ["a.cs"] = "class A {}" });

        Assert.Empty(index.Rank("   ", topN: 5));
    }
}
