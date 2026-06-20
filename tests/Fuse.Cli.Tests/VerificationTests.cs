using Fuse.Cli.Verification;
using Fuse.Emission.Models;

namespace Fuse.Cli.Tests;

public sealed class RegexApiSurfaceAnalyzerTests
{
    private readonly RegexApiSurfaceAnalyzer _analyzer = new();

    [Fact]
    public void Collect_ExtractsPublicTypesMethodsAndRoutes()
    {
        const string source = """
            using Microsoft.AspNetCore.Mvc;
            [Route("api/orders")]
            public class OrdersController
            {
                [HttpGet("{id}")]
                public IActionResult Get(int id) => Ok();
                private void Helper() { }
            }
            """;

        var types = new HashSet<string>();
        var methods = new HashSet<string>();
        var routes = new HashSet<string>();
        _analyzer.Collect(source, types, methods, routes);

        Assert.Contains("OrdersController", types);
        Assert.Contains("Get", methods);
        Assert.DoesNotContain("Helper", methods); // private
        Assert.Contains("api/orders", routes);
        Assert.Contains("{id}", routes);
    }

    [Fact]
    public void Collect_IgnoresTypeNamesInComments()
    {
        const string source = "// public class GhostType\npublic class Real { }";
        var types = new HashSet<string>();
        _analyzer.Collect(source, types, new HashSet<string>(), new HashSet<string>());

        Assert.Contains("Real", types);
        Assert.DoesNotContain("GhostType", types);
    }
}

public sealed class ApiSurfaceVerifierTests
{
    [Fact]
    public void Verify_AllSymbolsPresent_ReportsFullPreservation()
    {
        var verifier = new ApiSurfaceVerifier(ApiSurfaceAnalyzerFactory.Create());
        const string source = "public class Widget { public void Spin() { } }";
        const string fused = "public class Widget { public void Spin() { } }";

        var report = verifier.Verify([source], fused);

        Assert.Equal(1, report.FileCount);
        Assert.Equal(1.0, report.Types.Ratio);
        Assert.Equal(1.0, report.Methods.Ratio);
    }

    [Fact]
    public void Verify_MissingType_ReportsPartialPreservation()
    {
        var verifier = new ApiSurfaceVerifier(ApiSurfaceAnalyzerFactory.Create());
        const string source = "public class Kept { } public class Dropped { }";
        const string fused = "public class Kept { }";

        var report = verifier.Verify([source], fused);

        Assert.Equal(2, report.Types.Total);
        Assert.Equal(1, report.Types.Preserved);
        Assert.Equal(0.5, report.Types.Ratio);
    }

    [Fact]
    public void Verify_EmptyCategory_RatioIsOne()
    {
        var verifier = new ApiSurfaceVerifier(ApiSurfaceAnalyzerFactory.Create());
        var report = verifier.Verify([], string.Empty);
        Assert.Equal(1.0, report.Routes.Ratio);
    }
}

public sealed class ExplanationBuilderTests
{
    [Fact]
    public void Build_PartitionsIncludedAndExcluded()
    {
        var included = new[] { new FileTokenInfo("A.cs", 100), new FileTokenInfo("B.cs", 50) };
        var collected = new[] { "A.cs", "B.cs", "C.cs", "D.cs" };

        var lines = ExplanationBuilder.Build("focus 'A' depth 1", "o200k_base", included, collected);

        Assert.Contains("Scope: focus 'A' depth 1", lines);
        Assert.Contains("Tokenizer: o200k_base", lines);
        Assert.Contains("Included: 2 files, ~150 tokens", lines);
        Assert.Contains("  + A.cs (~100)", lines);
        Assert.Contains("Excluded: 2 files", lines);
        Assert.Contains("  - C.cs", lines);
        Assert.Contains("  - D.cs", lines);
    }

    [Fact]
    public void Build_OrdersIncludedByTokenDescending()
    {
        var included = new[] { new FileTokenInfo("small.cs", 10), new FileTokenInfo("big.cs", 900) };
        var lines = ExplanationBuilder.Build("all", "o200k_base", included, ["small.cs", "big.cs"]);

        var bigIndex = lines.ToList().FindIndex(l => l.Contains("big.cs"));
        var smallIndex = lines.ToList().FindIndex(l => l.Contains("small.cs"));
        Assert.True(bigIndex < smallIndex);
    }
}
