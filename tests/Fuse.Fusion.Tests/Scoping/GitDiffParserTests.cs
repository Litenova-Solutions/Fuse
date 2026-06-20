using Fuse.Fusion.Scoping;

namespace Fuse.Fusion.Tests.Scoping;

public class GitDiffParserTests
{
    [Fact]
    public void Parse_Empty_ReturnsEmpty()
    {
        Assert.Empty(GitDiffParser.Parse(null));
        Assert.Empty(GitDiffParser.Parse(string.Empty));
    }

    [Fact]
    public void Parse_SingleFile_ExtractsPathHunksAndCounts()
    {
        const string diff = """
            diff --git a/src/Order.cs b/src/Order.cs
            index 1111111..2222222 100644
            --- a/src/Order.cs
            +++ b/src/Order.cs
            @@ -1,3 +1,4 @@
             public class Order
             {
            -    public int Id;
            +    public int Id { get; set; }
            +    public string Name { get; set; }
             }
            """;

        var result = GitDiffParser.Parse(diff);

        var file = Assert.Single(result);
        Assert.Equal("src/Order.cs", file.Path);
        Assert.Equal(2, file.Added);
        Assert.Equal(1, file.Removed);
        Assert.Contains("@@ -1,3 +1,4 @@", file.Hunks);
        Assert.Contains("public string Name", file.Hunks);
    }

    [Fact]
    public void Parse_MultipleFiles_ReturnsOnePerFile()
    {
        const string diff = """
            diff --git a/A.cs b/A.cs
            --- a/A.cs
            +++ b/A.cs
            @@ -1 +1 @@
            -old
            +new
            diff --git a/B.cs b/B.cs
            --- a/B.cs
            +++ b/B.cs
            @@ -1 +1,2 @@
             keep
            +added
            """;

        var result = GitDiffParser.Parse(diff);

        Assert.Equal(2, result.Count);
        Assert.Equal("A.cs", result[0].Path);
        Assert.Equal("B.cs", result[1].Path);
        Assert.Equal(1, result[1].Added);
        Assert.Equal(0, result[1].Removed);
    }

    [Fact]
    public void Parse_NewFile_UsesTargetPath()
    {
        const string diff = """
            diff --git a/New.cs b/New.cs
            new file mode 100644
            index 0000000..3333333
            --- /dev/null
            +++ b/New.cs
            @@ -0,0 +1,2 @@
            +line one
            +line two
            """;

        var file = Assert.Single(GitDiffParser.Parse(diff));
        Assert.Equal("New.cs", file.Path);
        Assert.Equal(2, file.Added);
        Assert.Equal(0, file.Removed);
    }
}
