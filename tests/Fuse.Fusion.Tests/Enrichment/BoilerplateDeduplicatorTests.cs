using Fuse.Collection.Models;
using Fuse.Fusion.Enrichment;
using Fuse.Reduction.Models;
using Fuse.Reduction.Tokenization;

namespace Fuse.Fusion.Tests.Enrichment;

public sealed class BoilerplateDeduplicatorTests
{
    private static readonly BoilerplateDeduplicator Deduplicator = new();
    private static readonly ITokenCounter Counter = new LengthTokenCounter();

    private const string License =
        "// Copyright (c) Example Corp. All rights reserved.\n// Licensed under the MIT License.";

    [Fact]
    public void Deduplicate_SharedHeader_MovedToPreambleAndMarked()
    {
        var entries = new[]
        {
            Entry("A.cs", License + "\npublic class A { }"),
            Entry("B.cs", License + "\npublic class B { }"),
        };

        var result = Deduplicator.Deduplicate(entries, Counter);

        Assert.Equal(1, result.HeadersDeduplicated);
        Assert.Equal(2, result.FilesAffected);
        Assert.NotNull(result.Preamble);
        Assert.Contains("Copyright (c) Example Corp", result.Preamble);

        foreach (var entry in result.Content)
        {
            Assert.Contains("// fuse:header[1]", entry.Content);
            Assert.DoesNotContain("Licensed under the MIT License", entry.Content);
        }

        // The class bodies survive; only the comment header is moved.
        Assert.Contains("public class A", result.Content[0].Content);
        Assert.Contains("public class B", result.Content[1].Content);
    }

    [Fact]
    public void Deduplicate_HeaderInOnlyOneFile_IsLeftAlone()
    {
        var entries = new[]
        {
            Entry("A.cs", License + "\npublic class A { }"),
            Entry("B.cs", "public class B { }"),
        };

        var result = Deduplicator.Deduplicate(entries, Counter);

        Assert.Equal(0, result.HeadersDeduplicated);
        Assert.Null(result.Preamble);
        Assert.Contains("Licensed under the MIT License", result.Content[0].Content);
    }

    [Fact]
    public void Deduplicate_DoesNotTouchPreprocessorDirectives()
    {
        var entries = new[]
        {
            Entry("A.cs", "#nullable enable\npublic class A { }"),
            Entry("B.cs", "#nullable enable\npublic class B { }"),
        };

        var result = Deduplicator.Deduplicate(entries, Counter);

        Assert.Equal(0, result.HeadersDeduplicated);
        Assert.Contains("#nullable enable", result.Content[0].Content);
    }

    private static FusedContent Entry(string path, string content)
    {
        var candidate = new FileCandidate(path, path, new FileInfo(path));
        return new FusedContent(new SourceFile(candidate), content, Counter);
    }

    private sealed class LengthTokenCounter : ITokenCounter
    {
        public int Count(string content) => content.Length;
    }
}
