using Fuse.Semantics;
using Microsoft.CodeAnalysis;
using Xunit;
using Xunit.Abstractions;

namespace Fuse.Semantics.Tests;

// P3.2: MSBuild/Roslyn workspace loading with a guarded locator and a clean syntax fallback.
[Trait("Category", "RequiresSdk")]
public sealed class RoslynWorkspaceLoaderTests
{
    private readonly ITestOutputHelper _output;

    public RoslynWorkspaceLoaderTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task SyntaxOnlyDiscoveryDoesNotAttemptMsBuild()
    {
        var loader = new RoslynWorkspaceLoader();
        var discovery = new WorkspaceDiscoveryResult(WorkspaceKind.SyntaxOnly, null, [], "/tmp/none");

        var snapshot = await loader.LoadAsync(discovery, CancellationToken.None);

        Assert.False(snapshot.SemanticLoadSucceeded);
        Assert.Empty(snapshot.Projects);
        Assert.Contains(snapshot.Diagnostics, d => d.Code == "syntax-only");
    }

    [Fact]
    public async Task LoadsSampleShopCoreProjectSemantically()
    {
        var projectPath = FixturePath("SampleShop.Core", "SampleShop.Core.csproj");
        var discovery = new WorkspaceDiscoveryResult(
            WorkspaceKind.Projects, null, [projectPath], Path.GetDirectoryName(projectPath)!);

        var loader = new RoslynWorkspaceLoader();
        var snapshot = await loader.LoadAsync(discovery, CancellationToken.None);

        foreach (var diagnostic in snapshot.Diagnostics)
            _output.WriteLine($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");

        Assert.True(snapshot.SemanticLoadSucceeded, "expected semantic load to succeed for a clean SDK project");
        var project = Assert.Single(snapshot.Projects);
        Assert.Contains(
            project.Compilation.GetSymbolsWithName("OrderService", SymbolFilter.Type),
            _ => true);
    }

    private static string FixturePath(string projectDir, string file)
    {
        // Walk up from the test output directory to the repo root, then into tests/fixtures.
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "tests", "fixtures")))
            dir = Path.GetDirectoryName(dir);

        Assert.NotNull(dir);
        return Path.Combine(dir!, "tests", "fixtures", "SampleShop", "src", projectDir, file);
    }
}
