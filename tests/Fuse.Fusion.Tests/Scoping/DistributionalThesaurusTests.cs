using Fuse.Fusion.Scoping;

namespace Fuse.Fusion.Tests.Scoping;

public sealed class DistributionalThesaurusTests
{
    private static IReadOnlyList<IReadOnlySet<string>> Docs(params string[][] docs) =>
        docs.Select(d => (IReadOnlySet<string>)new HashSet<string>(d, StringComparer.Ordinal)).ToList();

    [Fact]
    public void Expand_AssociatesCoOccurringIdentifier()
    {
        // "order" and "payment" co-occur in three files; "http"/"client" are unrelated.
        var docs = Docs(
            ["order", "payment"],
            ["order", "payment"],
            ["order", "payment"],
            ["http", "client"]);

        var associates = DistributionalThesaurus.Expand(["order"], docs);

        Assert.Contains("payment", associates.Keys);
        Assert.DoesNotContain("http", associates.Keys);
        Assert.DoesNotContain("order", associates.Keys); // never returns the query term itself
    }

    [Fact]
    public void Expand_IgnoresSingleCoOccurrence()
    {
        // "order" and "payment" co-occur twice (above the minimum, positive PMI); "order"/"stray" only once.
        var docs = Docs(
            ["order", "payment"],
            ["order", "payment"],
            ["order", "stray"],
            ["unrelatedA"],
            ["unrelatedB"]);

        var associates = DistributionalThesaurus.Expand(["order"], docs);

        Assert.DoesNotContain("stray", associates.Keys);
        Assert.Contains("payment", associates.Keys);
    }

    [Fact]
    public void Expand_NoQueryTerms_IsEmpty()
    {
        Assert.Empty(DistributionalThesaurus.Expand([], Docs(["a", "b"])));
    }

    [Fact]
    public void Expand_AbsentQueryTerm_IsEmpty()
    {
        Assert.Empty(DistributionalThesaurus.Expand(["missing"], Docs(["a", "b"], ["a", "c"])));
    }
}
