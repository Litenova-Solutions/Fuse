using System.Reflection;
using Fuse.Cli.Mcp;
using Fuse.Collection.Templates;
using Fuse.Fusion;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Reduction.Security;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Tests.Mcp;

public sealed class FuseToolsTests
{
    [Theory]
    [InlineData(nameof(FuseTools.FuseTocAsync), "fuse_toc")]
    [InlineData(nameof(FuseTools.FuseAskAsync), "fuse_ask")]
    [InlineData(nameof(FuseTools.FuseSkeletonAsync), "fuse_skeleton")]
    [InlineData(nameof(FuseTools.FuseFocusAsync), "fuse_focus")]
    [InlineData(nameof(FuseTools.FuseSearchAsync), "fuse_search")]
    [InlineData(nameof(FuseTools.FuseChangesAsync), "fuse_changes")]
    [InlineData(nameof(FuseTools.FuseDotNetAsync), "fuse_dotnet")]
    [InlineData(nameof(FuseTools.FuseGenericAsync), "fuse_generic")]
    [InlineData(nameof(FuseTools.FuseReduceAsync), "fuse_reduce")]
    [InlineData(nameof(FuseTools.FuseExplainAsync), "fuse_explain")]
    [InlineData(nameof(FuseTools.FuseFindAsync), "fuse_find")]
    public void ToolMethods_AreRegisteredWithExpectedNames(string methodName, string expectedToolName)
    {
        var method = typeof(FuseTools).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var attribute = method!.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal(expectedToolName, attribute!.Name);
    }

    [Theory]
    [InlineData(nameof(FuseResources.ReadSkeletonResourceAsync), "fuse://skeleton/{path}")]
    [InlineData(nameof(FuseResources.ReadFocusResourceAsync), "fuse://focus/{path}/{seed}")]
    [InlineData(nameof(FuseResources.ReadSearchResourceAsync), "fuse://search/{path}/{query}")]
    [InlineData(nameof(FuseResources.ReadChangesResourceAsync), "fuse://changes/{path}/{since}")]
    public void ResourceMethods_UseWorkflowUriTemplates(string methodName, string expectedTemplate)
    {
        var method = typeof(FuseResources).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var attribute = method!.GetCustomAttribute<McpServerResourceAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal(expectedTemplate, attribute!.UriTemplate);
    }

    [Theory]
    [InlineData(nameof(FuseTools.FuseFocusAsync))]
    [InlineData(nameof(FuseTools.FuseSearchAsync))]
    [InlineData(nameof(FuseTools.FuseChangesAsync))]
    [InlineData(nameof(FuseTools.FuseDotNetAsync))]
    public void ScopedTools_DefaultLevelIsStandard(string methodName)
    {
        var method = typeof(FuseTools).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var levelParam = method!.GetParameters().Single(p => p.Name == "level");
        Assert.Equal(typeof(Plugins.Abstractions.Options.ReductionLevel), levelParam.ParameterType);
        Assert.Equal(Plugins.Abstractions.Options.ReductionLevel.Standard, levelParam.DefaultValue);
    }

    [Fact]
    public void ScopedTools_ExposeLevelInsteadOfBooleanCluster()
    {
        var method = typeof(FuseTools).GetMethod(nameof(FuseTools.FuseDotNetAsync), BindingFlags.Public | BindingFlags.Static);
        var paramNames = method!.GetParameters().Select(p => p.Name).ToHashSet();

        Assert.Contains("level", paramNames);
        foreach (var removed in new[] { "all", "aggressive", "skeleton", "removeCSharpComments", "removeCSharpUsings" })
            Assert.DoesNotContain(removed, paramNames);
    }

    [Fact]
    public async Task FuseTocAsync_EmitsTableOfContentsForDirectory()
    {
        using var fixture = new TempProject();
        fixture.AddFile("Services/OrderService.cs", """
            public class OrderService
            {
                public void PlaceOrder() { }
            }
            """);

        var (orchestrator, templates) = BuildServices();
        var result = await FuseTools.FuseTocAsync(orchestrator, templates, fixture.ProjectPath);

        Assert.Contains("fuse:table-of-contents", result);
        Assert.Contains("OrderService.cs", result);
        Assert.Contains("class OrderService: PlaceOrder", result);
        Assert.DoesNotContain("<file path", result);
    }

    [Fact]
    public async Task FuseAskAsync_NamedType_FocusesAndAnnotatesStrategy()
    {
        using var fixture = new TempProject();
        fixture.AddFile("Services/OrderService.cs", """
            public class OrderService
            {
                public void PlaceOrder() { }
            }
            """);

        var (orchestrator, templates) = BuildServices();
        var result = await FuseTools.FuseAskAsync(
            orchestrator, templates, fixture.ProjectPath, "How does OrderService place an order", tokenBudget: 20000);

        Assert.Contains("fuse_ask: strategy=focus", result);
        Assert.Contains("OrderService", result);
    }

    [Fact]
    public async Task FuseAskAsync_BroadQuestion_UsesSkeleton()
    {
        using var fixture = new TempProject();
        fixture.AddFile("Order.cs", """
            public class Order
            {
                public void Place() { var total = 1 + 2; }
            }
            """);

        var (orchestrator, templates) = BuildServices();
        var result = await FuseTools.FuseAskAsync(
            orchestrator, templates, fixture.ProjectPath, "Give me an architecture overview", tokenBudget: 20000);

        Assert.Contains("fuse_ask: strategy=skeleton", result);
        // Skeleton keeps signatures but drops method bodies.
        Assert.DoesNotContain("var total = 1 + 2", result);
    }

    [Fact]
    public async Task FuseAskAsync_EmptyTask_ReturnsError()
    {
        var (orchestrator, templates) = BuildServices();
        var result = await FuseTools.FuseAskAsync(orchestrator, templates, Path.GetTempPath(), "  ");
        Assert.StartsWith("Error", result);
    }

    [Fact]
    public async Task FuseReduceAsync_ReducesExplicitFiles()
    {
        using var fixture = new TempProject();
        fixture.AddFile("Sample.cs", """
            public class Sample
            {
                public int Add(int x, int y)
                {
                    return x + y + 8675309;
                }
            }
            """);

        var (orchestrator, templates) = BuildServices();
        var result = await FuseTools.FuseReduceAsync(
            orchestrator, templates, fixture.ProjectPath, files: ["Sample.cs"], level: ReductionLevel.Skeleton);

        Assert.Contains("Sample.cs", result);
        Assert.Contains("Add", result);
        Assert.DoesNotContain("8675309", result);
    }

    [Fact]
    public async Task FuseFocusPreset_MatchesEquivalentFuseDotNetInvocation()
    {
        // Behavior parity (Phase 4.3): the fuse_focus preset routes through the same shared path as the
        // full-control fuse_dotnet tool, so the set of emitted files is identical for an equivalent call.
        using var fixture = new TempProject();
        fixture.AddFile("Seed.cs", "public class Seed { public Dep D { get; set; } }");
        fixture.AddFile("Dep.cs", "public class Dep { public int Id { get; set; } }");
        fixture.AddFile("Other.cs", "public class Other { public string Name => \"x\"; }");

        var (orchestrator, templates) = BuildServices();

        // Both default the level to standard, so only the scoping path differs in how it is expressed.
        var preset = await FuseTools.FuseFocusAsync(orchestrator, templates, fixture.ProjectPath, "Seed", depth: 1);
        var full = await FuseTools.FuseDotNetAsync(orchestrator, templates, fixture.ProjectPath, focus: "Seed", depth: 1);

        Assert.Equal(EmittedFiles(preset), EmittedFiles(full));
    }

    [Fact]
    public async Task FuseExplainAsync_FocusScope_ListsIncludedFilesWithoutBodies()
    {
        using var fixture = new TempProject();
        fixture.AddFile("Seed.cs", "public class Seed { public Dep D { get; set; } }");
        fixture.AddFile("Dep.cs", "public class Dep { public int Id { get; set; } }");
        fixture.AddFile("Other.cs", "public class Other { public string Name => \"unique-body-token\"; }");

        var provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<FusionOrchestrator>();
        var templates = provider.GetRequiredService<ProjectTemplateRegistry>();
        var collection = provider.GetRequiredService<Fuse.Collection.FileCollectionPipeline>();

        var result = await FuseTools.FuseExplainAsync(
            orchestrator, templates, collection, fixture.ProjectPath, focus: "Seed", depth: 1);

        // The preview names the scoped files and the unrelated one, but emits no file bodies.
        Assert.Contains("Seed.cs", result);
        Assert.Contains("Dep.cs", result);
        Assert.Contains("Other.cs", result); // listed as excluded
        Assert.DoesNotContain("unique-body-token", result);
        Assert.DoesNotContain("<file path", result);
    }

    [Fact]
    public async Task FuseFindAsync_SymbolMode_FindsTypeAndMemberDeclarations()
    {
        using var fixture = new TempProject();
        fixture.AddFile("Services/OrderService.cs", """
            public class OrderService
            {
                public void PlaceOrder() { }
            }
            """);
        fixture.AddFile("Other.cs", "public class Other { public void PlaceOrder() { } }");

        var (provider, templates, collection, redactor) = BuildFindServices();

        var typeHit = await FuseTools.FuseFindAsync(
            templates, collection, provider, redactor, OutlineRegistry(), fixture.ProjectPath, "OrderService", FindMode.Symbol);
        Assert.Contains("OrderService.cs: class OrderService", typeHit);

        // A member name resolves in every declaring file and is reported with its owning type.
        var memberHit = await FuseTools.FuseFindAsync(
            templates, collection, provider, redactor, OutlineRegistry(), fixture.ProjectPath, "PlaceOrder", FindMode.Symbol);
        Assert.Contains("member PlaceOrder in OrderService", memberHit);
        Assert.Contains("member PlaceOrder in Other", memberHit);
    }

    [Fact]
    public async Task FuseFindAsync_TextMode_ReturnsMatchWithContextAndLineNumber()
    {
        using var fixture = new TempProject();
        fixture.AddFile("Sample.cs", """
            public class Sample
            {
                public int Magic() => 8675309;
            }
            """);

        var (provider, templates, collection, redactor) = BuildFindServices();
        var result = await FuseTools.FuseFindAsync(
            templates, collection, provider, redactor, OutlineRegistry(), fixture.ProjectPath, "8675309", FindMode.Text);

        Assert.Contains("Sample.cs:3", result);
        Assert.Contains("8675309", result);
    }

    [Fact]
    public async Task FuseFindAsync_TextMode_RedactsSecretsInSnippets()
    {
        using var fixture = new TempProject();
        const string marker = "FUSE_FIND_SECRET_MARKER";
        fixture.AddFile("Config.cs", $$"""
            public class Config
            {
                public string ApiKey = "AKIAIOSFODNN7EXAMPLE";
                public string Label = "{{marker}}";
            }
            """);

        var (provider, templates, collection, redactor) = BuildFindServices();
        var result = await FuseTools.FuseFindAsync(
            templates, collection, provider, redactor, OutlineRegistry(), fixture.ProjectPath, marker, FindMode.Text);

        Assert.Contains(marker, result);
        Assert.Contains("[REDACTED:", result);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", result);
    }

    [Fact]
    public async Task FuseFindAsync_PathMode_MatchesPathFragment()
    {
        using var fixture = new TempProject();
        fixture.AddFile("Services/OrderService.cs", "public class OrderService { }");
        fixture.AddFile("Models/Order.cs", "public class Order { }");

        var (provider, templates, collection, redactor) = BuildFindServices();
        var result = await FuseTools.FuseFindAsync(
            templates, collection, provider, redactor, OutlineRegistry(), fixture.ProjectPath, "Services/", FindMode.Path);

        Assert.Contains("Services/OrderService.cs", result);
        Assert.DoesNotContain("Models/Order.cs", result);
    }

    [Fact]
    public async Task FuseFindAsync_EmptyQuery_ReturnsError()
    {
        var (provider, templates, collection, redactor) = BuildFindServices();
        var result = await FuseTools.FuseFindAsync(
            templates, collection, provider, redactor, OutlineRegistry(), Path.GetTempPath(), "  ");
        Assert.StartsWith("Error", result);
    }

    [Fact]
    public async Task FuseFindAsync_NoMatch_ReportsNoneFound()
    {
        using var fixture = new TempProject();
        fixture.AddFile("Sample.cs", "public class Sample { }");

        var (provider, templates, collection, redactor) = BuildFindServices();
        var result = await FuseTools.FuseFindAsync(
            templates, collection, provider, redactor, OutlineRegistry(), fixture.ProjectPath, "DoesNotExist", FindMode.Symbol);

        Assert.Contains("No symbol named 'DoesNotExist'", result);
    }

    private static (
        Func<Fuse.Collection.FileSystem.ISourceContentProvider>,
        ProjectTemplateRegistry,
        Fuse.Collection.FileCollectionPipeline,
        ISecretRedactor) BuildFindServices()
    {
        var provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();
        return (
            provider.GetRequiredService<Func<Fuse.Collection.FileSystem.ISourceContentProvider>>(),
            provider.GetRequiredService<ProjectTemplateRegistry>(),
            provider.GetRequiredService<Fuse.Collection.FileCollectionPipeline>(),
            provider.GetRequiredService<ISecretRedactor>());
    }

    private static Plugins.Abstractions.CapabilityRegistry<Plugins.Abstractions.Outline.ISymbolOutlineExtractor> OutlineRegistry() =>
        new ServiceCollection().AddFuseForTests().BuildServiceProvider()
            .GetRequiredService<Plugins.Abstractions.CapabilityRegistry<Plugins.Abstractions.Outline.ISymbolOutlineExtractor>>();

    private static IReadOnlyList<string> EmittedFiles(string output) =>
        System.Text.RegularExpressions.Regex.Matches(output, "<file path=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    private static (FusionOrchestrator, ProjectTemplateRegistry) BuildServices() =>
        FuseToolsTestHost.BuildServices();

    private sealed class TempProject : FuseToolsTestHost.TempProject;
}

internal static class FuseToolsTestHost
{
    internal static (FusionOrchestrator, ProjectTemplateRegistry) BuildServices()
    {
        var provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();
        return (
            provider.GetRequiredService<FusionOrchestrator>(),
            provider.GetRequiredService<ProjectTemplateRegistry>());
    }

    internal static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Fuse.slnx")))
                return dir;

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Could not locate repository root (Fuse.slnx).");
    }

    internal class TempProject : IDisposable
    {
        public string ProjectPath { get; } =
            Path.Combine(Path.GetTempPath(), "fuse-mcp-tests", Guid.NewGuid().ToString("N"));

        public TempProject() => Directory.CreateDirectory(ProjectPath);

        public void AddFile(string relativePath, string content)
        {
            var full = Path.Combine(ProjectPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(ProjectPath))
                Directory.Delete(ProjectPath, recursive: true);
        }
    }
}
