using Fuse.Fusion.Scoping;

namespace Fuse.Fusion.Tests.Scoping;

public class ChangeReviewBuilderTests
{
    [Fact]
    public void Build_NoDiffs_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ChangeReviewBuilder.Build([], EmptyCallers()));
    }

    [Fact]
    public void Build_RendersHunksAndCallers()
    {
        var diffs = new[]
        {
            new FileDiff("src/Order.cs", 2, 1, "@@ -1 +1,2 @@\n-old\n+new\n+more"),
        };
        var callers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/Order.cs"] = ["src/OrderService.cs", "src/Cart.cs"],
        };

        var review = ChangeReviewBuilder.Build(diffs, callers);

        Assert.Contains("fuse:review 1 changed file", review);
        Assert.Contains("=== review: src/Order.cs (+2 -1) ===", review);
        Assert.Contains("callers (2): src/Cart.cs, src/OrderService.cs", review);
        Assert.Contains("@@ -1 +1,2 @@", review);
    }

    [Fact]
    public void Build_NoCallers_StatesNoneDetected()
    {
        var diffs = new[] { new FileDiff("A.cs", 1, 0, "@@ -0,0 +1 @@\n+x") };

        var review = ChangeReviewBuilder.Build(diffs, EmptyCallers());

        Assert.Contains("callers: none detected", review);
    }

    [Fact]
    public void ComputeCallers_FindsFilesReferencingDeclaredTypes()
    {
        var declaredTypes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/Order.cs"] = ["Order"],
        };
        var typeReferences = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Order"] = ["src/OrderService.cs", "src/Order.cs"],
        };
        var graph = new DependencyGraph(
            fileReferences: new Dictionary<string, IReadOnlyList<string>>(),
            typeIndex: new Dictionary<string, IReadOnlyList<string>>(),
            declaredTypes: declaredTypes,
            typeReferences: typeReferences);

        var callers = ChangeReviewBuilder.ComputeCallers(["src/Order.cs"], graph);

        // The file itself is excluded; only its referrers remain.
        Assert.Equal(["src/OrderService.cs"], callers["src/Order.cs"]);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyCallers() =>
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}
