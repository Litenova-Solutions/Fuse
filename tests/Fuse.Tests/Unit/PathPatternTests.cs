using Fuse.Paths;

namespace Fuse.Tests.Unit;

/// <summary>
///     A path pattern matches repository-relative paths: <c>**</c> across segments, <c>*</c> and <c>?</c> inside one,
///     and a bare name as a file or a directory subtree. A pattern that cannot be matched is refused.
/// </summary>
public class PathPatternTests
{
    [Theory]
    [InlineData("Lib/**", "Lib/Calc.cs", true)]
    [InlineData("Lib/**", "Lib/Sub/Calc.cs", true)]
    [InlineData("Lib/**", "Lib", false)]
    [InlineData("src/Workers/**", "src/Workers/Third/Worker.cs", true)]
    [InlineData("**/*.cs", "Lib/Calc.cs", true)]
    [InlineData("**/*.cs", "Calc.cs", true)]
    [InlineData("Lib/**/*.cs", "Lib/Calc.cs", true)]
    [InlineData("Lib/**/*.cs", "App/Program.cs", false)]
    public void Double_star_matches_zero_or_more_segments(string pattern, string path, bool matches)
    {
        Assert.True(PathPattern.Normalize(pattern, out var clean));
        Assert.Equal(matches, PathPattern.Matches(clean, path));
    }

    [Theory]
    [InlineData("Lib/*.cs", "Lib/Calc.cs", true)]
    [InlineData("Lib/*.cs", "Lib/Sub/Calc.cs", false)]
    [InlineData("Lib/Calc?.cs", "Lib/Calc1.cs", true)]
    [InlineData("Lib/Calc?.cs", "Lib/Calc.cs", false)]
    [InlineData("Lib/*.cs", "Lib/Lib.csproj", false)]
    public void Star_and_question_mark_stay_inside_one_segment(string pattern, string path, bool matches)
    {
        Assert.True(PathPattern.Normalize(pattern, out var clean));
        Assert.Equal(matches, PathPattern.Matches(clean, path));
    }

    [Theory]
    [InlineData("Lib/Calc.cs", "Lib/Calc.cs", true)]
    [InlineData("Lib", "Lib/Calc.cs", true)]
    [InlineData("Lib/", "Lib/Sub/Calc.cs", true)]
    [InlineData("Lib", "Library/Calc.cs", false)]
    [InlineData("Lib/Calc.cs", "Lib/Other.cs", false)]
    public void A_bare_name_is_a_file_or_a_directory_subtree(string pattern, string path, bool matches)
    {
        Assert.True(PathPattern.Normalize(pattern, out var clean));
        Assert.Equal(matches, PathPattern.Matches(clean, path));
    }

    [Fact]
    public void Matching_compares_the_way_the_file_system_does()
    {
        Assert.True(PathPattern.Normalize("lib/**/*.cs", out var clean));
        Assert.Equal(OperatingSystem.IsWindows(), PathPattern.Matches(clean, "Lib/Calc.cs"));
        Assert.True(PathPattern.Normalize("Lib", out clean));
        Assert.Equal(OperatingSystem.IsWindows(), PathPattern.Matches(clean, "LIB/Calc.cs"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Lib/../App")]
    [InlineData("Lib/a**b/*.cs")]
    [InlineData("Lib/**b/*.cs")]
    public void A_pattern_that_cannot_be_matched_is_refused(string pattern)
    {
        Assert.False(PathPattern.Normalize(pattern, out _));
    }

    [Fact]
    public void A_pattern_longer_than_the_limit_is_refused()
    {
        Assert.False(PathPattern.Normalize(new string('a', PathPattern.MaxLength + 1), out _));
        Assert.True(PathPattern.Normalize(new string('a', PathPattern.MaxLength), out _));
    }

    [Fact]
    public void Backslashes_read_as_slashes_and_a_trailing_slash_reads_as_the_directory()
    {
        Assert.True(PathPattern.Normalize("Lib\\**\\*.cs", out var clean));
        Assert.Equal("Lib/**/*.cs", clean);
        Assert.True(PathPattern.Normalize("Lib/", out clean));
        Assert.Equal("Lib", clean);
    }
}
