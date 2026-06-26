using Fuse.Fusion.Session;

namespace Fuse.Fusion.Tests.Session;

public sealed class UnifiedDiffGeneratorTests
{
    [Fact]
    public void Build_IdenticalContent_ReturnsNull()
    {
        Assert.Null(UnifiedDiffGenerator.Build("a\nb\nc", "a\nb\nc"));
    }

    [Fact]
    public void Build_SingleLineChange_ShowsDeleteAndInsertWithContext()
    {
        var diff = UnifiedDiffGenerator.Build("a\nb\nc", "a\nB\nc");

        Assert.NotNull(diff);
        Assert.Contains("@@ -1,3 +1,3 @@", diff);
        Assert.Contains("\n-b\n", "\n" + diff);
        Assert.Contains("\n+B\n", "\n" + diff);
        Assert.Contains(" a\n", diff); // context line, leading space
        Assert.Contains(" c\n", diff);
    }

    [Fact]
    public void Build_PureInsertion_ShowsOnlyAddedLine()
    {
        var diff = UnifiedDiffGenerator.Build("a\nc", "a\nb\nc");

        Assert.NotNull(diff);
        Assert.Contains("+b", diff);
        Assert.DoesNotContain("\n-", "\n" + diff); // nothing deleted
    }

    [Fact]
    public void Build_PureDeletion_ShowsOnlyRemovedLine()
    {
        var diff = UnifiedDiffGenerator.Build("a\nb\nc", "a\nc");

        Assert.NotNull(diff);
        Assert.Contains("-b", diff);
        Assert.DoesNotContain("\n+", "\n" + diff); // nothing inserted
    }

    [Fact]
    public void Build_FarApartChanges_DropsContextBetweenHunks()
    {
        // A change at the top and bottom of a 20-line file; the middle (far from both) is not emitted.
        var before = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"line{i}"));
        var after = before.Replace("line0", "LINE0").Replace("line19", "LINE19");

        var diff = UnifiedDiffGenerator.Build(before, after);

        Assert.NotNull(diff);
        Assert.Contains("LINE0", diff);
        Assert.Contains("LINE19", diff);
        Assert.DoesNotContain("line10", diff); // mid-file context far from any change is omitted
        // Two separate hunks, so two headers.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(diff!, "@@ ").Count);
    }

    [Fact]
    public void Build_MostlyChanged_ReturnsNullForWholeFileResend()
    {
        // Every line differs, so a diff is not smaller than the file: signal a whole-file resend.
        var before = string.Join("\n", Enumerable.Range(0, 10).Select(i => $"old{i}"));
        var after = string.Join("\n", Enumerable.Range(0, 10).Select(i => $"new{i}"));

        Assert.Null(UnifiedDiffGenerator.Build(before, after));
    }

    [Fact]
    public void Build_HugeFile_ReturnsNull()
    {
        var before = string.Join("\n", Enumerable.Range(0, 2500).Select(i => $"line{i}"));
        var after = before + "\nextra";

        Assert.Null(UnifiedDiffGenerator.Build(before, after));
    }
}
