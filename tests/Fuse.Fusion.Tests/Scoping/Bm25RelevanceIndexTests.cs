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

    [Fact]
    public void Rank_SymbolField_OutranksIncidentalBodyMention()
    {
        var index = new Bm25RelevanceIndex();
        index.Index(new Dictionary<string, IndexedDocument>
        {
            // Declares the concept as a symbol.
            ["Domain/LedgerEntry.cs"] = new IndexedDocument(
                "public class LedgerEntry { }",
                "Domain/LedgerEntry.cs",
                ["LedgerEntry"]),
            // Only mentions the concept incidentally in the body, many times.
            ["Util/Logging.cs"] = new IndexedDocument(
                "// ledger ledger ledger ledger ledger ledger ledger\npublic class Logging { }",
                "Util/Logging.cs",
                ["Logging"]),
        });

        var ranked = index.Rank("ledger", topN: 1);

        Assert.Single(ranked);
        Assert.Equal("Domain/LedgerEntry.cs", ranked[0]);
    }

    [Fact]
    public void RankScored_ReturnsDescendingScores()
    {
        var index = new Bm25RelevanceIndex();
        index.Index(new Dictionary<string, IndexedDocument>
        {
            ["Payments/PaymentService.cs"] = new IndexedDocument(
                "public class PaymentService { public void ProcessPayment() {} }",
                "Payments/PaymentService.cs",
                ["PaymentService", "ProcessPayment"]),
            ["Catalog/ProductService.cs"] = new IndexedDocument(
                "public class ProductService { }",
                "Catalog/ProductService.cs",
                ["ProductService"]),
        });

        var ranked = index.RankScored("payment", topN: 5);

        Assert.NotEmpty(ranked);
        Assert.Equal("Payments/PaymentService.cs", ranked[0].Path);
        for (var i = 1; i < ranked.Count; i++)
            Assert.True(ranked[i - 1].Score >= ranked[i].Score);
    }
}
