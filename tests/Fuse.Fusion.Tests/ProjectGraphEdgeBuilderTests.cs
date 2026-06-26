using Fuse.Fusion.Scoping;

namespace Fuse.Fusion.Tests;

// Item 8: the coarse project-reference graph links a file to candidate files in the projects its .csproj
// references or is referenced by, so a seed reaches a related file across an assembly boundary the intra-project
// type graph misses.
public sealed class ProjectGraphEdgeBuilderTests : IDisposable
{
    private readonly string _root;

    public ProjectGraphEdgeBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuse-pg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "Lib"));
        Directory.CreateDirectory(Path.Combine(_root, "test", "Lib.Tests"));

        // A library project and a test project that references it (the common .NET layout).
        File.WriteAllText(Path.Combine(_root, "src", "Lib", "Lib.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(Path.Combine(_root, "test", "Lib.Tests", "Lib.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n" +
            "    <ProjectReference Include=\"..\\..\\src\\Lib\\Lib.csproj\" />\n  </ItemGroup>\n</Project>");

        File.WriteAllText(Path.Combine(_root, "src", "Lib", "Widget.cs"), "namespace Lib; public class Widget {}");
        File.WriteAllText(Path.Combine(_root, "test", "Lib.Tests", "WidgetTests.cs"),
            "namespace Lib.Tests; public class WidgetTests {}");
    }

    [Fact]
    public void Build_LinksFilesAcrossAProjectReference()
    {
        var edges = ProjectGraphEdgeBuilder.Build(
            _root, ["src/Lib/Widget.cs", "test/Lib.Tests/WidgetTests.cs"]);

        // The reference is recorded both ways, so the library file reaches the test and vice versa.
        Assert.True(edges.TryGetValue("src/Lib/Widget.cs", out var fromLib));
        Assert.Contains("test/Lib.Tests/WidgetTests.cs", fromLib!);

        Assert.True(edges.TryGetValue("test/Lib.Tests/WidgetTests.cs", out var fromTest));
        Assert.Contains("src/Lib/Widget.cs", fromTest!);
    }

    [Fact]
    public void Build_DoesNotLinkFilesWithinTheSameProject()
    {
        File.WriteAllText(Path.Combine(_root, "src", "Lib", "Gadget.cs"), "namespace Lib; public class Gadget {}");

        var edges = ProjectGraphEdgeBuilder.Build(_root, ["src/Lib/Widget.cs", "src/Lib/Gadget.cs"]);

        // Same-project files are left to the intra-project type graph, not the coarse cross-project edge.
        Assert.False(edges.ContainsKey("src/Lib/Widget.cs") && edges["src/Lib/Widget.cs"].Contains("src/Lib/Gadget.cs"));
    }

    [Fact]
    public void Build_NoProjects_ReturnsEmpty()
    {
        var empty = Path.Combine(Path.GetTempPath(), "fuse-pg-empty", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            Assert.Empty(ProjectGraphEdgeBuilder.Build(empty, ["a.cs"]));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
