using Fuse.Indexing;
using Fuse.Retrieval;
using Xunit;

namespace Fuse.Retrieval.Tests;

// T2: the public-API delta between two symbol sets. Removal, signature change, and accessibility reduction are
// breaking; an addition is additive; an internal-only change is not on the surface. Flagging is conservative.
public sealed class PublicApiDeltaTests
{
    private static SymbolRecord Member(string fqn, string signature, string accessibility = "public") =>
        new($"symbol:{fqn}", "src/Api.cs", "method", fqn.Split('.').Last(), fqn,
            Accessibility: accessibility, Signature: signature, IsPublicApi: accessibility is "public" or "protected");

    [Fact]
    public void Removed_public_member_is_breaking()
    {
        var result = PublicApiDelta.Compute(
            [Member("Api.Svc.Foo", "public void Foo()"), Member("Api.Svc.Bar", "public void Bar()")],
            [Member("Api.Svc.Foo", "public void Foo()")]);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ApiChangeKind.Removed, change.Kind);
        Assert.Equal("Api.Svc.Bar", change.Symbol);
        Assert.True(change.Breaking);
        Assert.True(result.HasBreaking);
    }

    [Fact]
    public void Added_public_member_is_additive_not_breaking()
    {
        var result = PublicApiDelta.Compute(
            [Member("Api.Svc.Foo", "public void Foo()")],
            [Member("Api.Svc.Foo", "public void Foo()"), Member("Api.Svc.Baz", "public void Baz()")]);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ApiChangeKind.Added, change.Kind);
        Assert.Equal("Api.Svc.Baz", change.Symbol);
        Assert.False(change.Breaking);
        Assert.False(result.HasBreaking);
    }

    [Fact]
    public void Signature_change_is_breaking()
    {
        var result = PublicApiDelta.Compute(
            [Member("Api.Svc.Foo", "public void Foo()")],
            [Member("Api.Svc.Foo", "public void Foo(int x)")]);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ApiChangeKind.SignatureChanged, change.Kind);
        Assert.True(change.Breaking);
    }

    [Fact]
    public void Accessibility_reduction_public_to_protected_is_breaking()
    {
        // public -> protected: both are on the public surface, so the reduction is reported as such.
        var result = PublicApiDelta.Compute(
            [Member("Api.Svc.Foo", "void Foo()", "public")],
            [Member("Api.Svc.Foo", "void Foo()", "protected")]);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ApiChangeKind.AccessibilityReduced, change.Kind);
        Assert.Equal("public", change.Before);
        Assert.Equal("protected", change.After);
        Assert.True(change.Breaking);
    }

    [Fact]
    public void Public_to_internal_leaves_the_surface_and_reports_removed()
    {
        // public -> internal: the member is no longer on the public surface, so it reads as a breaking removal.
        var result = PublicApiDelta.Compute(
            [Member("Api.Svc.Foo", "void Foo()", "public")],
            [Member("Api.Svc.Foo", "void Foo()", "internal")]);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ApiChangeKind.Removed, change.Kind);
        Assert.True(change.Breaking);
    }

    [Fact]
    public void No_change_yields_an_empty_delta()
    {
        var symbols = new[] { Member("Api.Svc.Foo", "public void Foo()") };
        var result = PublicApiDelta.Compute(symbols, symbols);
        Assert.Empty(result.Changes);
        Assert.False(result.HasBreaking);
    }

    [Fact]
    public void Internal_only_changes_are_not_on_the_public_surface()
    {
        // Both sides internal: not part of the public surface, so removing/changing it is not reported.
        var result = PublicApiDelta.Compute(
            [Member("Api.Svc.Helper", "void Helper()", "internal")],
            []);
        Assert.Empty(result.Changes);
    }
}
