using Fuse.Benchmarks;
using Xunit;

namespace Fuse.Benchmarks.Tests;

// C4 4b: the pure logic that turns a commit's changed files into a candidate fail-to-pass oracle task - which
// files count as tests, and how the dotnet-test --filter is derived from the changed test classes. The git and
// test-execution parts are exercised by the corpus-health run on the real corpus; these pin the decisions that
// must be deterministic.
public sealed class CorpusTaskExtractorTests
{
    [Theory]
    [InlineData("test/Scrutor.Tests/ScanningTests.cs", true)]
    [InlineData("tests/Foo/BarSpecs.cs", true)]
    [InlineData("src/Widget/Widget.Tests.cs", true)]
    [InlineData("src/Scrutor/AssemblySelector.cs", false)]
    [InlineData("src/Widget/Widget.cs", false)]
    [InlineData("docs/readme.md", false)]
    public void IsTestFile_classifies_by_path(string path, bool expected)
        => Assert.Equal(expected, CorpusTaskExtractor.IsTestFile(path));

    [Fact]
    public void Classify_splits_test_and_source_files()
    {
        var (tests, sources) = CorpusTaskExtractor.Classify([
            "test/Foo.Tests/ThingTests.cs",
            "src/Foo/Thing.cs",
            "src/Foo/Helper.cs",
        ]);

        Assert.Equal(["test/Foo.Tests/ThingTests.cs"], tests);
        Assert.Equal(["src/Foo/Thing.cs", "src/Foo/Helper.cs"], sources);
    }

    [Fact]
    public void ExtractTestClassNames_finds_classes_named_like_tests()
    {
        const string source = """
            namespace Foo.Tests;
            public class ThingTests { }
            public class ThingSpec { }
            internal class Helper { }
            """;

        var names = CorpusTaskExtractor.ExtractTestClassNames(source).ToList();

        Assert.Contains("ThingTests", names);
        Assert.Contains("ThingSpec", names);
        Assert.DoesNotContain("Helper", names); // No test suffix and no test attribute nearby.
    }

    [Fact]
    public void ExtractTestClassNames_includes_all_classes_when_a_test_attribute_is_present()
    {
        const string source = """
            using Xunit;
            public class Scenarios
            {
                [Fact] public void Works() { }
            }
            """;

        var names = CorpusTaskExtractor.ExtractTestClassNames(source).ToList();

        // A [Fact] in the file means the declared class hosts tests even without a Tests suffix.
        Assert.Contains("Scenarios", names);
    }

    [Fact]
    public void BuildFilter_unions_class_names_by_fully_qualified_name()
    {
        var filter = CorpusTaskExtractor.BuildFilter(["ThingTests", "OtherTests"]);
        Assert.Equal("FullyQualifiedName~ThingTests|FullyQualifiedName~OtherTests", filter);
    }

    [Fact]
    public void BuildFilter_is_null_when_no_class_names()
        => Assert.Null(CorpusTaskExtractor.BuildFilter([]));
}
