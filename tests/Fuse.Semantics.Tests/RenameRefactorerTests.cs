using System.Runtime.CompilerServices;
using Fuse.Semantics;
using Xunit;

namespace Fuse.Semantics.Tests;

// R7: compiler-executed solution-wide rename staged as a diff. This is an integration test over the SampleShop
// fixture solution (MSBuild-loaded); it is tolerant of an environment where the solution does not load, in which
// case the refactorer must abstain with a reason rather than throw or produce a partial rename.
public sealed class RenameRefactorerTests
{
    private static string? SampleShopSolution([CallerFilePath] string sourceFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fuse.slnx")))
            dir = dir.Parent;
        if (dir is null)
            return null;
        var sln = Path.Combine(dir.FullName, "tests", "fixtures", "SampleShop", "SampleShop.sln");
        return File.Exists(sln) ? sln : null;
    }

    [Fact]
    public async Task Rename_stages_a_diff_or_abstains_cleanly()
    {
        var sln = SampleShopSolution();
        if (sln is null)
            return; // Fixture not present.

        // SecretsHolder is a known type in the fixture; rename it and expect a staged diff, or a clean abstention
        // if the solution does not load in this environment.
        var result = await new RenameRefactorer().RenameAsync(sln, "SecretsHolder", "SecretsBag", CancellationToken.None);

        if (!result.Renamed)
        {
            Assert.False(string.IsNullOrEmpty(result.Reason));
            return;
        }

        Assert.Equal("SecretsBag", result.NewName);
        Assert.NotEmpty(result.Diffs);
        // The staged diff mentions the new name; the change is staged, never written to the working tree.
        Assert.Contains(result.Diffs, d => d.UnifiedDiff.Contains("SecretsBag"));
    }

    [Fact]
    public async Task Missing_symbol_abstains()
    {
        var sln = SampleShopSolution();
        if (sln is null)
            return;
        var result = await new RenameRefactorer().RenameAsync(sln, "ThisTypeDoesNotExistAnywhere", "X", CancellationToken.None);
        Assert.False(result.Renamed);
        Assert.False(string.IsNullOrEmpty(result.Reason));
    }
}
