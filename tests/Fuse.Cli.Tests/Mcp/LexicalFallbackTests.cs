using Fuse.Cli.Mcp;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

// R30: the opt-in inline lexical fallback serves scoped, ranked raw-text matches tagged lexical-fallback when the
// index is not semantic-ready and FUSE_LEXICAL_FALLBACK is on, and never returns fewer than a literal scan.
public sealed class LexicalFallbackTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fuse-lexfb", Guid.NewGuid().ToString("N"));

    public LexicalFallbackTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void IsEnabled_DefaultsOff_AndOptsIn()
    {
        var original = Environment.GetEnvironmentVariable(LexicalFallback.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(LexicalFallback.EnvVar, null);
            Assert.False(LexicalFallback.IsEnabled());
            Environment.SetEnvironmentVariable(LexicalFallback.EnvVar, "1");
            Assert.True(LexicalFallback.IsEnabled());
        }
        finally
        {
            Environment.SetEnvironmentVariable(LexicalFallback.EnvVar, original);
        }
    }

    [Fact]
    public async Task Search_FindsAllMatchingFiles_TaggedLexicalFallback_NeverFewerThanGrep()
    {
        Write("src/A.cs", "public class Widget { }");
        Write("src/B.cs", "// uses Widget here\nclass Uses { }");
        Write("src/C.cs", "public class Other { }");
        Write("node_modules/pkg/D.cs", "class Widget { }"); // excluded scope: must not be counted.

        // A literal scan over the repo's own source finds "Widget" in exactly A.cs and B.cs.
        var expected = new[] { "src/A.cs", "src/B.cs" };

        var result = await LexicalFallback.SearchAsync(_root, "Widget", 50, CancellationToken.None);

        Assert.Contains("grade: lexical-fallback", result, StringComparison.Ordinal);
        Assert.Contains("lexical matches: 2 file(s)", result, StringComparison.Ordinal);
        foreach (var path in expected)
            Assert.Contains(path, result.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("node_modules", result, StringComparison.OrdinalIgnoreCase); // excluded scope.
        Assert.DoesNotContain("Other", result, StringComparison.Ordinal); // non-matching file not listed.
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsGradeWithNoQueryNote()
    {
        var result = await LexicalFallback.SearchAsync(_root, "", 50, CancellationToken.None);
        Assert.Contains("grade: lexical-fallback", result, StringComparison.Ordinal);
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
