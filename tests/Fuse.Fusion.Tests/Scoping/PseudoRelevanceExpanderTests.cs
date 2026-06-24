using Fuse.Fusion.Scoping;

namespace Fuse.Fusion.Tests.Scoping;

public sealed class PseudoRelevanceExpanderTests
{
    private static IndexedDocument Doc(string path, params string[] symbols) =>
        new("// body", path, symbols);

    // Default IDF stub: every term is discriminative enough to clear the floor.
    private static readonly Func<string, double> Discriminative = _ => 2.0;

    [Fact]
    public void Expand_AddsRecurringSymbolTerm_AtReducedWeight()
    {
        var ranking = new[]
        {
            new RankedFile("Mapping/MapperConfiguration.cs", 3.0),
            new RankedFile("Mapping/TypeMapFactory.cs", 2.0),
            new RankedFile("Mapping/MapperProfile.cs", 1.0),
        };
        var documents = new Dictionary<string, IndexedDocument>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mapping/MapperConfiguration.cs"] = Doc("Mapping/MapperConfiguration.cs", "MapperConfiguration", "TypeMap"),
            ["Mapping/TypeMapFactory.cs"] = Doc("Mapping/TypeMapFactory.cs", "TypeMapFactory", "TypeMap"),
            ["Mapping/MapperProfile.cs"] = Doc("Mapping/MapperProfile.cs", "MapperProfile"),
        };

        var expanded = PseudoRelevanceExpander.Expand(
            "configuration", ranking, documents, new QueryExpansionOptions(), Discriminative);

        Assert.Equal(1.0, expanded["configuration"]); // original query term, full weight
        // "TypeMap" recurs in two feedback files, so it is admitted as an expansion term below unit weight.
        Assert.True(expanded.ContainsKey("type"));
        Assert.True(expanded.ContainsKey("map"));
        Assert.Equal(0.2, expanded["type"]);
    }

    [Fact]
    public void Expand_GatesOutCorpusWideTermBelowIdfFloor()
    {
        var ranking = new[]
        {
            new RankedFile("A.cs", 3.0),
            new RankedFile("B.cs", 2.0),
            new RankedFile("C.cs", 1.0),
        };
        var documents = new Dictionary<string, IndexedDocument>(StringComparer.OrdinalIgnoreCase)
        {
            ["A.cs"] = Doc("A.cs", "OrderValidator", "Boilerplate"),
            ["B.cs"] = Doc("B.cs", "OrderValidator", "Boilerplate"),
            ["C.cs"] = Doc("C.cs", "Other"),
        };

        // "boilerplate" is corpus-wide (low IDF); "order"/"validator" are discriminative (high IDF).
        Func<string, double> idf = t => t == "boilerplate" ? 0.2 : 3.0;
        var expanded = PseudoRelevanceExpander.Expand("entity", ranking, documents, new QueryExpansionOptions(), idf);

        Assert.False(expanded.ContainsKey("boilerplate")); // below the IDF floor, dropped
        Assert.True(expanded.ContainsKey("order"));
        Assert.True(expanded.ContainsKey("validator"));
    }

    [Fact]
    public void Expand_DoesNotAddTermFromSingleFeedbackDoc()
    {
        var ranking = new[]
        {
            new RankedFile("A.cs", 3.0),
            new RankedFile("B.cs", 2.0),
            new RankedFile("C.cs", 1.0),
        };
        var documents = new Dictionary<string, IndexedDocument>(StringComparer.OrdinalIgnoreCase)
        {
            ["A.cs"] = Doc("A.cs", "Solitary"),
            ["B.cs"] = Doc("B.cs", "Common"),
            ["C.cs"] = Doc("C.cs", "Common"),
        };

        var expanded = PseudoRelevanceExpander.Expand(
            "query", ranking, documents, new QueryExpansionOptions(), Discriminative);

        // MinFeedbackDocs is 2: "solitary" appears in only one feedback file and must not be added.
        Assert.False(expanded.ContainsKey("solitary"));
        Assert.True(expanded.ContainsKey("common"));
    }

    [Fact]
    public void Expand_Disabled_ReturnsOriginalTermsOnly()
    {
        var ranking = new[] { new RankedFile("A.cs", 3.0), new RankedFile("B.cs", 2.0), new RankedFile("C.cs", 1.0) };
        var documents = new Dictionary<string, IndexedDocument>(StringComparer.OrdinalIgnoreCase)
        {
            ["A.cs"] = Doc("A.cs", "Common"),
            ["B.cs"] = Doc("B.cs", "Common"),
            ["C.cs"] = Doc("C.cs", "Common"),
        };

        var expanded = PseudoRelevanceExpander.Expand(
            "payment", ranking, documents, QueryExpansionOptions.Disabled, Discriminative);

        Assert.Equal(["payment"], expanded.Keys.OrderBy(k => k));
    }

    [Fact]
    public void Expand_SparseFirstPass_DoesNotExpand()
    {
        // Below MinInitialHits (3): a single weak hit is not trusted as feedback.
        var ranking = new[] { new RankedFile("A.cs", 1.0) };
        var documents = new Dictionary<string, IndexedDocument>(StringComparer.OrdinalIgnoreCase)
        {
            ["A.cs"] = Doc("A.cs", "Common"),
        };

        var expanded = PseudoRelevanceExpander.Expand(
            "payment", ranking, documents, new QueryExpansionOptions(), Discriminative);

        Assert.Equal(["payment"], expanded.Keys.OrderBy(k => k));
    }

    [Fact]
    public void MergePreservingSeeds_RetainsFirstPassSeedDemotedByExpansion()
    {
        var firstPass = new[]
        {
            new RankedFile("License.cs", 5.0),   // the genuinely relevant seed the vague query happened to hit
            new RankedFile("Casing.cs", 4.0),
        };
        var reranked = new[]
        {
            new RankedFile("Casing.cs", 9.0),     // expansion toward "lower case" promoted this
            new RankedFile("StringUtil.cs", 8.0), // and surfaced this
        };

        var merged = PseudoRelevanceExpander.MergePreservingSeeds(firstPass, reranked);
        var paths = merged.Select(r => r.Path).ToHashSet();

        // License.cs dropped out of the reranked window but must survive: expansion cannot lower recall.
        Assert.Contains("License.cs", paths);
        Assert.Contains("Casing.cs", paths);
        Assert.Contains("StringUtil.cs", paths);
        // A file present in both takes its reranked (expanded) score.
        Assert.Equal(9.0, merged.Single(r => r.Path == "Casing.cs").Score);
        // Ordering is by score descending.
        for (var i = 1; i < merged.Count; i++)
            Assert.True(merged[i - 1].Score >= merged[i].Score);
    }
}
