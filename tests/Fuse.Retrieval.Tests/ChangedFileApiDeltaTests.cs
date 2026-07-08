using Fuse.Retrieval;
using Xunit;

namespace Fuse.Retrieval.Tests;

// T2: the changed-file bridge extracts public symbols from base and current content (syntax only) and diffs the
// surface. These use in-memory content (no git) so the extraction-plus-diff behavior is pinned deterministically.
public sealed class ChangedFileApiDeltaTests
{
    [Fact]
    public void A_removed_public_method_is_a_breaking_change()
    {
        var result = ChangedFileApiDelta.Compute(
        [
            new ChangedFileContent(
                "src/Api.cs",
                BaseContent: "public class Api { public void Foo() { } public void Bar() { } }",
                CurrentContent: "public class Api { public void Foo() { } }"),
        ]);

        Assert.True(result.HasBreaking);
        Assert.Contains(result.Changes, c => c.Kind == ApiChangeKind.Removed && c.Symbol.Contains("Bar"));
    }

    [Fact]
    public void An_added_public_method_is_additive_not_breaking()
    {
        var result = ChangedFileApiDelta.Compute(
        [
            new ChangedFileContent(
                "src/Api.cs",
                BaseContent: "public class Api { public void Foo() { } }",
                CurrentContent: "public class Api { public void Foo() { } public void Baz() { } }"),
        ]);

        Assert.False(result.HasBreaking);
        Assert.Contains(result.Changes, c => c.Kind == ApiChangeKind.Added && c.Symbol.Contains("Baz"));
    }

    [Fact]
    public void A_private_member_change_does_not_appear_in_the_delta()
    {
        var result = ChangedFileApiDelta.Compute(
        [
            new ChangedFileContent(
                "src/Api.cs",
                BaseContent: "public class Api { private void Helper() { } }",
                CurrentContent: "public class Api { }"),
        ]);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void A_newly_added_file_has_no_base_content_and_yields_only_additions()
    {
        var result = ChangedFileApiDelta.Compute(
        [
            new ChangedFileContent(
                "src/New.cs",
                BaseContent: null,
                CurrentContent: "public class New { public void Created() { } }"),
        ]);

        Assert.False(result.HasBreaking);
        Assert.All(result.Changes, c => Assert.Equal(ApiChangeKind.Added, c.Kind));
        Assert.Contains(result.Changes, c => c.Symbol.Contains("Created"));
    }

    [Fact]
    public void A_non_csharp_file_contributes_nothing()
    {
        var result = ChangedFileApiDelta.Compute(
        [
            new ChangedFileContent("config.json", BaseContent: "{ \"a\": 1 }", CurrentContent: "{ \"a\": 2 }"),
        ]);

        Assert.Empty(result.Changes);
    }
}
