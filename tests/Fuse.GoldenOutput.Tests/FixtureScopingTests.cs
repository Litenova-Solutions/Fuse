using Fuse.Analysis.Dependencies;
using Fuse.Analysis.Search;

namespace Fuse.GoldenOutput.Tests;

public sealed class FixtureDeterminismTests
{
    [Fact]
    public async Task SampleShop_SerialAndParallel_OutputIsByteIdentical()
    {
        using var host = new GoldenFusionTestHost();
        var emission = new Fuse.Emission.Models.EmissionOptions { IncludeManifest = true, IncludeGitStats = false };

        var serial = await host.FuseSampleShopAsync(parallelism: 1);
        var parallel = await host.FuseSampleShopAsync(parallelism: 8);

        Assert.Equal(serial, parallel);
    }
}

public sealed class FixtureScopingTests
{
    [Fact]
    public async Task SampleShop_FocusOrderService_IncludesPaymentCluster()
    {
        using var host = new GoldenFusionTestHost();
        var output = await host.FuseSampleShopAsync(
            focus: new FocusOptions("OrderService", Depth: 2),
            emission: new Fuse.Emission.Models.EmissionOptions { IncludeManifest = false, IncludeGitStats = false });

        Assert.Contains("OrderService.cs", output);
        Assert.Contains("PaymentService.cs", output);
        Assert.Contains("PaymentGateway.cs", output);
        Assert.DoesNotContain("CatalogItem.cs", output);
        Assert.DoesNotContain("OrdersController.cs", output);
    }

    [Fact]
    public async Task SampleShop_QueryPayment_IncludesPaymentClusterOnly()
    {
        using var host = new GoldenFusionTestHost();
        var output = await host.FuseSampleShopAsync(
            query: new QueryOptions("payment process", TopFiles: 2, Depth: 1),
            emission: new Fuse.Emission.Models.EmissionOptions { IncludeManifest = false, IncludeGitStats = false });

        Assert.Contains("PaymentService.cs", output);
        Assert.Contains("PaymentGateway.cs", output);
        Assert.DoesNotContain("CatalogItem.cs", output);
        Assert.DoesNotContain("OrdersController.cs", output);
    }

    [Fact]
    public async Task SampleShop_DefaultFusion_RedactsPlantedSecrets()
    {
        using var host = new GoldenFusionTestHost();
        var output = await host.FuseSampleShopAsync(
            emission: new Fuse.Emission.Models.EmissionOptions { IncludeManifest = false, IncludeGitStats = false });

        Assert.Contains("[REDACTED:aws-access-key]", output);
        Assert.Contains("[REDACTED:jwt]", output);
        Assert.Contains("[REDACTED:connection-string]", output);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", output);
        Assert.DoesNotContain("SecretP@ssw0rd123", output);
        Assert.DoesNotContain("eyJhbGciOi", output);
    }
}
