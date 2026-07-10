using Fuse.Semantics.Remediation;
using Xunit;

namespace Fuse.Semantics.Tests;

// C1: the environment-remediation knowledge base. These tests exercise the matcher per signature against the
// bake-off failure classes (n4-bakeoff.json), confirming each maps to the expected remedy and consent posture,
// that repository-code failures are classify-only, and that unmatched output returns null. This is the item's
// first listed test (KB matcher unit tests per signature); the fuse up engine that applies remedies is a later
// sub-step.
public sealed class RemediationKnowledgeBaseTests
{
    private readonly RemediationKnowledgeBase _kb = RemediationKnowledgeBase.LoadDefault();

    [Fact]
    public void Default_knowledge_base_loads_the_bakeoff_signature_classes()
    {
        var ids = _kb.Signatures.Select(s => s.Id).ToList();
        Assert.Contains("NU1507", ids);
        Assert.Contains("NETSDK1045", ids);
        Assert.Contains("MSB4018", ids);
        Assert.Contains("CS0104", ids);
        Assert.Contains("CS2007", ids);
    }

    [Theory]
    [InlineData("error NU1507: There are 3 package sources defined", "NU1507", "overlay-nuget-source-mapping", false)]
    [InlineData("error NETSDK1045: The current .NET SDK does not support targeting .NET 10.0", "NETSDK1045", "install-sdk", true)]
    [InlineData("error MSB4018: The \"GenerateResource\" task failed unexpectedly", "MSB4018", "install-workload", true)]
    public void Environment_signatures_match_to_their_remedy_and_consent(
        string output, string expectedId, string expectedRemedy, bool expectedConsent)
    {
        var match = _kb.Match(output);
        Assert.NotNull(match);
        Assert.Equal(expectedId, match!.Id);
        Assert.Equal(expectedRemedy, match.Remedy);
        Assert.Equal(expectedConsent, match.RequiresConsent);
    }

    [Theory]
    [InlineData("Program.cs(10,20): error CS0104: 'Timer' is an ambiguous reference", "CS0104")]
    [InlineData("csc : error CS2007: Unrecognized option: '/langversion'", "CS2007")]
    public void Repository_code_failures_are_classify_only(string output, string expectedId)
    {
        var match = _kb.Match(output);
        Assert.NotNull(match);
        Assert.Equal(expectedId, match!.Id);
        Assert.Equal("classify-only", match.Remedy);
        Assert.False(match.RequiresConsent);
    }

    [Fact]
    public void Unmatched_output_returns_null()
    {
        Assert.Null(_kb.Match("Build succeeded. 0 Warning(s) 0 Error(s)"));
        Assert.Null(_kb.Match(""));
        Assert.Null(_kb.Match(null));
    }
}
