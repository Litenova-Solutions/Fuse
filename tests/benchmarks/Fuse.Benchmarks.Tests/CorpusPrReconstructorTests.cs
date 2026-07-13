using Fuse.Benchmarks;
using Xunit;

namespace Fuse.Benchmarks.Tests;

// D22c: the pure selection rules for reconstructing a review/localize/ranking PR ground-truth set from a corpus
// repository's merge history. The git walk is wired in the runner; these pin the title filter and file-count band
// so a corpus-v2 PR set is reproduced deterministically.
public sealed class CorpusPrReconstructorTests
{
    [Theory]
    [InlineData("Merge pull request #123 from foo/bar")]
    [InlineData("Merge branch 'main' into feature")]
    [InlineData("Bump Newtonsoft.Json from 12.0.1 to 13.0.1")]
    [InlineData("Update dependencies")]
    [InlineData("Revert \"Add caching\"")]
    [InlineData("Prepare release 4.2.0")]
    [InlineData("v3.1.0")]
    [InlineData("Update CHANGELOG")]
    [InlineData("Fix formatting")]
    [InlineData("   ")]
    public void Maintenance_titles_are_dropped(string title)
    {
        Assert.True(CorpusPrReconstructor.IsMaintenanceTitle(title));
    }

    [Theory]
    [InlineData("Add retry policy to the HTTP client")]
    [InlineData("Fix null reference when parsing an empty header")]
    [InlineData("Support keyed service registration")]
    [InlineData("Reworked the version comparison logic")] // "version" mid-title, not a version bump
    public void Real_change_titles_are_kept(string title)
    {
        Assert.False(CorpusPrReconstructor.IsMaintenanceTitle(title));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(25, true)]
    [InlineData(26, false)]
    [InlineData(100, false)]
    public void File_count_band_is_two_to_twenty_five(int count, bool qualifies)
    {
        Assert.Equal(qualifies, CorpusPrReconstructor.QualifiesByFileCount(count));
    }

    [Fact]
    public void Qualifies_requires_both_a_real_title_and_a_banded_file_count()
    {
        Assert.True(CorpusPrReconstructor.Qualifies("Add a feature", new[] { "a.cs", "b.cs" }));
        Assert.False(CorpusPrReconstructor.Qualifies("Bump version", new[] { "a.cs", "b.cs" })); // maintenance title
        Assert.False(CorpusPrReconstructor.Qualifies("Add a feature", new[] { "only.cs" }));      // too few files
    }

    [Fact]
    public void ToRecord_carries_the_changed_files_as_ground_truth()
    {
        var record = CorpusPrReconstructor.ToRecord(
            "Scrutor", 7, "mergeSha", "baseSha", "Add scanning", ["src/Scanner.cs", "src/Registry.cs"]);

        Assert.Equal("Scrutor", record.Repo);
        Assert.Equal(7, record.Pr);
        Assert.Equal("mergeSha", record.Merge);
        Assert.Equal("baseSha", record.Base);
        Assert.Equal("Add scanning", record.Title);
        Assert.Equal(["src/Scanner.cs", "src/Registry.cs"], record.ChangedCs);
    }
}
