using Fuse.Semantics.Analyzers;
using Xunit;

namespace Fuse.Semantics.Tests;

// R5 part 2: tests edges from a test type to the source types it references, with DI resolution linking an
// injected interface to its registered implementation. Foreign-key safe: edges are emitted only to node ids
// that already exist in the merged graph.
public sealed class TestEdgeExtractorTests
{
    private const string Source = """
        namespace App;

        public sealed class FactAttribute : System.Attribute { }

        public interface IThing { void Do(); }
        public sealed class Thing : IThing { public void Do() { } }
        public sealed class Unregistered { public void M() { } }

        public sealed class ThingTests
        {
            [Fact]
            public void Uses_the_interface(IThing thing) { thing.Do(); }
        }
        """;

    [Fact]
    public void Links_a_test_to_a_referenced_type_and_its_di_implementation()
    {
        var project = InlineCompilation.Load(Source);

        // The merged graph already has nodes for the interface and its implementation; DI resolves IThing -> Thing.
        var existing = new HashSet<string>(StringComparer.Ordinal)
        {
            "type:App.IThing", "type:App.Thing", "type:App.Unregistered",
        };
        var di = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["type:App.IThing"] = ["type:App.Thing"],
        };

        var (nodes, edges) = new TestEdgeExtractor()
            .Extract([project], existing, di, "/repo", CancellationToken.None);

        var tests = edges.Where(e => e.EdgeType == "tests" && e.FromNodeId == "type:App.ThingTests").ToList();
        // Direct reference to the interface.
        Assert.Contains(tests, e => e.ToNodeId == "type:App.IThing");
        // DI-resolved implementation, even though the test never names Thing directly.
        Assert.Contains(tests, e => e.ToNodeId == "type:App.Thing");
        // The test type is materialized as a node so the edge's from-side foreign key resolves.
        Assert.Contains(nodes, n => n.NodeId == "type:App.ThingTests");
        // Every tests edge carries the tests weight.
        Assert.All(tests, e => Assert.Equal(0.65, e.Weight));
    }

    [Fact]
    public void Does_not_emit_edges_to_nodes_that_do_not_exist()
    {
        var project = InlineCompilation.Load(Source);
        // Only IThing is a known node; Thing is absent, so the DI-resolved impl edge must not be emitted (FK-safe).
        var existing = new HashSet<string>(StringComparer.Ordinal) { "type:App.IThing" };
        var di = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["type:App.IThing"] = ["type:App.Thing"],
        };

        var (_, edges) = new TestEdgeExtractor()
            .Extract([project], existing, di, "/repo", CancellationToken.None);

        Assert.Contains(edges, e => e.EdgeType == "tests" && e.ToNodeId == "type:App.IThing");
        Assert.DoesNotContain(edges, e => e.EdgeType == "tests" && e.ToNodeId == "type:App.Thing");
    }

    [Fact]
    public void A_non_test_type_produces_no_tests_edges()
    {
        // Same source but treat only non-test references: a type with no test-attributed method is not a test.
        var project = InlineCompilation.Load("""
            namespace App;
            public interface IThing { void Do(); }
            public sealed class Consumer { public void Use(IThing t) { t.Do(); } }
            """);
        var existing = new HashSet<string>(StringComparer.Ordinal) { "type:App.IThing" };
        var (_, edges) = new TestEdgeExtractor().Extract(
            [project], existing, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal), "/repo", CancellationToken.None);

        Assert.DoesNotContain(edges, e => e.EdgeType == "tests");
    }
}
