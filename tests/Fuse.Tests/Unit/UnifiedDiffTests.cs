using Fuse.Engine;

namespace Fuse.Tests.Unit;

public class UnifiedDiffTests
{
    private const string ModifyDiff =
        "diff --git a/file.cs b/file.cs\n" +
        "--- a/file.cs\n" +
        "+++ b/file.cs\n" +
        "@@ -1,3 +1,3 @@\n" +
        " line1\n" +
        "-line2\n" +
        "+line2 changed\n" +
        " line3\n";

    [Fact]
    public void Modify_applies_a_hunk_with_context()
    {
        var (files, parseError) = UnifiedDiff.Parse(ModifyDiff);
        Assert.Null(parseError);
        var patch = Assert.Single(files);
        Assert.Equal("file.cs", patch.Path);
        Assert.False(patch.IsNew);
        Assert.False(patch.IsDeleted);

        var (text, error) = UnifiedDiff.ApplyToText("line1\nline2\nline3\n", patch);
        Assert.Null(error);
        Assert.Equal("line1\nline2 changed\nline3\n", text);
    }

    [Fact]
    public void Apply_keeps_crlf_line_endings()
    {
        var (files, _) = UnifiedDiff.Parse(ModifyDiff);
        var patch = Assert.Single(files);

        var (text, error) = UnifiedDiff.ApplyToText("line1\r\nline2\r\nline3\r\n", patch);
        Assert.Null(error);
        Assert.Equal("line1\r\nline2 changed\r\nline3\r\n", text);
    }

    [Fact]
    public void Add_creates_a_new_file_from_nothing()
    {
        var (files, parseError) = UnifiedDiff.Parse(
            "diff --git a/new.cs b/new.cs\n" +
            "new file mode 100644\n" +
            "--- /dev/null\n" +
            "+++ b/new.cs\n" +
            "@@ -0,0 +1,2 @@\n" +
            "+line1\n" +
            "+line2\n");
        Assert.Null(parseError);
        var patch = Assert.Single(files);
        Assert.True(patch.IsNew);
        Assert.False(patch.IsDeleted);
        Assert.Equal("new.cs", patch.Path);

        var (text, error) = UnifiedDiff.ApplyToText("", patch);
        Assert.Null(error);
        Assert.Equal("line1\nline2\n", text);
    }

    [Fact]
    public void Delete_removes_the_file_text()
    {
        var (files, parseError) = UnifiedDiff.Parse(
            "diff --git a/old.cs b/old.cs\n" +
            "deleted file mode 100644\n" +
            "--- a/old.cs\n" +
            "+++ /dev/null\n" +
            "@@ -1,2 +0,0 @@\n" +
            "-line1\n" +
            "-line2\n");
        Assert.Null(parseError);
        var patch = Assert.Single(files);
        Assert.True(patch.IsDeleted);
        Assert.False(patch.IsNew);
        Assert.Equal("old.cs", patch.Path);

        var (text, error) = UnifiedDiff.ApplyToText("line1\nline2\n", patch);
        Assert.Null(error);
        Assert.Equal("", text);
    }

    [Fact]
    public void Mismatched_hunk_fails_with_file_hunk_and_line()
    {
        var (files, _) = UnifiedDiff.Parse(ModifyDiff);
        var patch = Assert.Single(files);

        var (text, error) = UnifiedDiff.ApplyToText("line1\nWRONG\nline3\n", patch);
        Assert.Null(text);
        Assert.Contains("file.cs", error, StringComparison.Ordinal);
        Assert.Contains("hunk 1", error, StringComparison.Ordinal);
        Assert.Contains("line 2", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Multiple_files_parse_in_order()
    {
        var (files, parseError) = UnifiedDiff.Parse(
            ModifyDiff +
            "diff --git a/other.cs b/other.cs\n" +
            "--- a/other.cs\n" +
            "+++ b/other.cs\n" +
            "@@ -1 +1 @@\n" +
            "-old\n" +
            "+new\n");
        Assert.Null(parseError);
        Assert.Equal(["file.cs", "other.cs"], files.Select(f => f.Path));
    }

    [Fact]
    public void Missing_trailing_newline_is_kept()
    {
        var (files, parseError) = UnifiedDiff.Parse(
            "--- a/f.cs\n" +
            "+++ b/f.cs\n" +
            "@@ -1,2 +1,2 @@\n" +
            " a\n" +
            "-b\n" +
            "\\ No newline at end of file\n" +
            "+B\n" +
            "\\ No newline at end of file\n");
        Assert.Null(parseError);
        var patch = Assert.Single(files);

        var (text, error) = UnifiedDiff.ApplyToText("a\nb", patch);
        Assert.Null(error);
        Assert.Equal("a\nB", text);
    }

    [Fact]
    public void Empty_diff_has_no_files()
    {
        var (files, parseError) = UnifiedDiff.Parse("");
        Assert.Null(parseError);
        Assert.Empty(files);
    }

    [Fact]
    public void Trailing_blank_lines_after_the_last_hunk_are_ignored()
    {
        var (files, parseError) = UnifiedDiff.Parse(ModifyDiff + "\n");
        Assert.Null(parseError);
        var patch = Assert.Single(files);

        var (text, error) = UnifiedDiff.ApplyToText("line1\nline2\nline3\n", patch);
        Assert.Null(error);
        Assert.Equal("line1\nline2 changed\nline3\n", text);
    }

    [Fact]
    public void Broken_hunk_header_fails_with_reason()
    {
        var (files, error) = UnifiedDiff.Parse("--- a/f.cs\n+++ b/f.cs\n@@ nonsense @@\n");
        Assert.Empty(files);
        Assert.NotNull(error);
    }
}
