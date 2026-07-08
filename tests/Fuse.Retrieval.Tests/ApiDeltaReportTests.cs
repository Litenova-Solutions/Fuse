using Fuse.Indexing;
using Fuse.Retrieval;
using Xunit;

namespace Fuse.Retrieval.Tests;

// T2: the API-delta section fuse_review/fuse_impact prepend. Breaking changes are flagged, additive listed, and
// an unchanged surface reads as a single "none" line.
public sealed class ApiDeltaReportTests
{
    private static SymbolRecord Member(string fqn, string signature, string accessibility = "public") =>
        new($"symbol:{fqn}", "src/Api.cs", "method", fqn.Split('.').Last(), fqn,
            Accessibility: accessibility, Signature: signature, IsPublicApi: accessibility is "public" or "protected");

    [Fact]
    public void No_change_renders_a_single_none_line()
    {
        var symbols = new[] { Member("Api.Foo", "public void Foo()") };
        var text = ApiDeltaReport.Render(PublicApiDelta.Compute(symbols, symbols));
        Assert.Contains("public API delta: none", text);
    }

    [Fact]
    public void A_breaking_removal_is_flagged_breaking()
    {
        var text = ApiDeltaReport.Render(PublicApiDelta.Compute(
            [Member("Api.Foo", "public void Foo()"), Member("Api.Bar", "public void Bar()")],
            [Member("Api.Foo", "public void Foo()")]));

        Assert.Contains("BREAKING change(s)", text);
        Assert.Contains("[BREAKING] Api.Bar: removed", text);
    }

    [Fact]
    public void An_addition_is_flagged_additive()
    {
        var text = ApiDeltaReport.Render(PublicApiDelta.Compute(
            [Member("Api.Foo", "public void Foo()")],
            [Member("Api.Foo", "public void Foo()"), Member("Api.Baz", "public void Baz()")]));

        Assert.Contains("none breaking", text);
        Assert.Contains("[additive] Api.Baz: added", text);
    }

    [Fact]
    public void A_signature_change_shows_before_and_after()
    {
        var text = ApiDeltaReport.Render(PublicApiDelta.Compute(
            [Member("Api.Foo", "public void Foo()")],
            [Member("Api.Foo", "public void Foo(int x)")]));

        Assert.Contains("[BREAKING] Api.Foo: signature changed (public void Foo() -> public void Foo(int x))", text);
    }
}
