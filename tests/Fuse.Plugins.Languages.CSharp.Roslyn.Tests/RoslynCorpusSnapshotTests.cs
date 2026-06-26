using System.Reflection;
using Fuse.Plugins.Languages.CSharp.Roslyn;

namespace Fuse.Plugins.Languages.CSharp.Roslyn.Tests;

public sealed class RoslynCorpusSnapshotTests
{
    private static string ReadFixture(string relativePath)
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Combine(repoRoot, "tests", "fixtures", "SampleShop", relativePath));
    }

    [Fact]
    public void OrderService_Skeleton_MatchesGolden()
    {
        var source = ReadFixture("src/SampleShop.Core/Services/OrderService.cs");
        var actual = new RoslynSkeletonExtractor().ExtractSkeleton(source);
        RoslynGoldenAssert.AssertMatches("order-service-skeleton", actual);
    }

    [Fact]
    public void OrderService_Outline_MatchesGolden()
    {
        var source = ReadFixture("src/SampleShop.Core/Services/OrderService.cs");
        var outline = new RoslynOutlineExtractor().ExtractOutline(source);
        var actual = string.Join('\n', outline.Select(entry =>
            $"{entry.Kind} {entry.Name}: {string.Join(", ", entry.Members)}"));
        RoslynGoldenAssert.AssertMatches("order-service-outline", actual);
    }

    [Fact]
    public void OrdersController_Dependencies_MatchesGolden()
    {
        var source = ReadFixture("src/SampleShop.Web/Controllers/OrdersController.cs");
        var refs = new RoslynDependencyExtractor().ExtractReferencedTypes(source);
        var actual = string.Join('\n', refs.Order(StringComparer.Ordinal));
        RoslynGoldenAssert.AssertMatches("orders-controller-dependencies", actual);
    }

    private static string ResolveRepoRoot()
    {
        var start = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var current = new DirectoryInfo(start);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Fuse.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root containing Fuse.slnx.");
    }
}

internal static class RoslynGoldenAssert
{
    public static void AssertMatches(string scenarioName, string actual)
    {
        var repoRoot = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
            "..", "..", "..");
        repoRoot = Path.GetFullPath(repoRoot);

        var expectedPath = Path.Combine(repoRoot, "expected", scenarioName + ".golden");
        var outputPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "expected", scenarioName + ".golden");

        if (string.Equals(Environment.GetEnvironmentVariable("UPDATE_GOLDEN_FILES"), "1", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, actual, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, actual, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;
        }

        Assert.True(File.Exists(outputPath), $"Golden file missing: {outputPath}. Set UPDATE_GOLDEN_FILES=1 to generate.");
        var expected = File.ReadAllText(outputPath);
        Assert.Equal(NormalizeLineEndings(expected), NormalizeLineEndings(actual));
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n");
}
