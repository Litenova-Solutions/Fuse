using Fuse.Fusion.Scoping;

namespace Fuse.Fusion.Tests.Scoping;

public sealed class CommentExtractorTests
{
    [Fact]
    public void Extract_PullsLineBlockAndDocComments()
    {
        const string source = """
            // line comment about retries
            /// <summary>Doc comment about throttling.</summary>
            public class C
            {
                /* block comment about backoff */
                public void M() { } // trailing note
            }
            """;

        var comments = CommentExtractor.Extract(source);

        Assert.Contains("retries", comments);
        Assert.Contains("throttling", comments);
        Assert.Contains("backoff", comments);
        Assert.Contains("trailing note", comments);
        // Code identifiers are not comment text.
        Assert.DoesNotContain("public class C", comments);
    }

    [Fact]
    public void Extract_NoComments_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, CommentExtractor.Extract("public class C { public void M() { } }"));
        Assert.Equal(string.Empty, CommentExtractor.Extract(string.Empty));
    }
}
