using Fuse.Indexing;
using Fuse.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Fuse.Semantics.Tests;

// P3.3: semantic symbol extraction and stable symbol ids.
public sealed class SemanticSymbolExtractorTests
{
    private const string Source = """
        namespace App.Services
        {
            public interface IOrderService { void Place(int id); }

            public class OrderService : IOrderService
            {
                private int _count;
                public OrderService(int seed) => _count = seed;
                public void Place(int id) { }
                internal int Pending => _count;
            }
        }
        """;

    [Fact]
    public void SymbolIdIsStableAcrossCompilations()
    {
        var id1 = BuildOrderServiceTypeId();
        var id2 = BuildOrderServiceTypeId();

        Assert.Equal(id1, id2);
        Assert.StartsWith("symbol:", id1);
    }

    [Fact]
    public void ExtractsTypesAndMembersWithResolvedMetadata()
    {
        var project = LoadProject(Source);
        var extractor = new SemanticSymbolExtractor();

        var records = extractor.Extract(project, "/repo", CancellationToken.None);

        var type = Assert.Single(records, r => r.Name == "OrderService" && r.Kind == "class");
        Assert.Equal("App.Services", type.Namespace);
        Assert.Equal("Public", type.Accessibility);
        Assert.True(type.IsPublicApi);
        Assert.StartsWith("symbol:", type.SymbolId);

        Assert.Contains(records, r => r.Name == "IOrderService" && r.Kind == "interface" && r.IsPublicApi);
        Assert.Contains(records, r => r.Name == "Place" && r.Kind == "method");
        Assert.Contains(records, r => r.Kind == "constructor");
        // An internal property is extracted but not part of the public API.
        Assert.Contains(records, r => r.Name == "Pending" && !r.IsPublicApi);
    }

    [Fact]
    public void SkipsPropertyAccessorMethods()
    {
        var project = LoadProject(Source);
        var records = new SemanticSymbolExtractor().Extract(project, "/repo", CancellationToken.None);

        Assert.DoesNotContain(records, r => r.Name.StartsWith("get_", StringComparison.Ordinal));
        Assert.DoesNotContain(records, r => r.Name.StartsWith("set_", StringComparison.Ordinal));
    }

    private static string BuildOrderServiceTypeId()
    {
        var project = LoadProject(Source);
        var type = project.Compilation.GetSymbolsWithName("OrderService", SymbolFilter.Type).Single();
        return SymbolIdBuilder.Build(type);
    }

    private static LoadedProject LoadProject(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "/repo/src/OrderService.cs");
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        };
        var compilation = CSharpCompilation.Create(
            "App",
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new LoadedProject("App", "/repo/src/App.csproj", "App", compilation);
    }
}
