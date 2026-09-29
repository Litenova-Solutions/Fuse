using Fuse.Workspace;

namespace Fuse.Tests.Unit;

/// <summary>
///     MSBuildWorkspace reports every message MSBuild logged while loading a project, warnings included, as a failure
///     of that project. These cases pin which of those messages may leave Fuse unable to answer.
/// </summary>
public class LoadFailureTests
{
    [Theory]
    // The two advisory messages captured from real engine logs, in the exact wording MSBuild's event carries.
    [InlineData("Package 'SQLitePCLRaw.lib.e_sqlite3' 2.1.11 has a known high severity vulnerability, https://github.com/advisories/GHSA-2m69-gcr7-jv3q [C:\\p\\a.csproj::TargetFramework=net10.0]", true)]
    [InlineData("Package 'Microsoft.Build.Tasks.Git' 8.0.0 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-23fw-v26w-5fgq", true)]
    [InlineData("Package 'A' 1.0.0 has a known critical severity vulnerability, https://example.test/a", true)]
    [InlineData("NU1603: Dependency X does not contain an inclusive lower bound", true)]
    [InlineData("The project file could not be loaded. Could not find project 'C:\\p\\missing.csproj'", false)]
    [InlineData("MSB4019: The imported project C:\\p\\Directory.Build.targets was not found", false)]
    [InlineData("CS9057: Analyzer assembly cannot be used because it references a newer compiler", false)]
    [InlineData("error NU1101: Could not find package X", true)]
    [InlineData("", false)]
    public void Classifies_warnings_and_failures(string message, bool isWarning) =>
        Assert.Equal(isWarning, LoadFailure.IsWarning(message));

    [Fact]
    public void An_advisory_url_alone_is_not_enough()
    {
        // The warning is recognised by NuGet's fixed wording or its NU code, not by a link in free text.
        Assert.False(LoadFailure.IsWarning("See https://github.com/advisories/GHSA-2m69-gcr7-jv3q for details"));
    }

    [Fact]
    public void A_version_range_or_a_prerelease_suffix_still_reads_as_an_advisory()
    {
        // NuGet puts the resolved version between the name and the wording, and it can be a range or carry a suffix.
        Assert.True(LoadFailure.IsWarning("Package 'A' 1.0.0-beta.1 has a known moderate severity vulnerability, https://example.test/a"));
        Assert.True(LoadFailure.IsWarning("Package 'A' [1.0.0, 2.0.0) has a known low severity vulnerability, https://example.test/a"));
    }

    [Fact]
    public void A_code_from_another_tool_is_a_failure()
    {
        // NU is NuGet's; MSB and CS are not, and a project that failed for one of those really did fail to load.
        Assert.False(LoadFailure.IsWarning("MSB4018: The referenced project 'C:\\p\\x.csproj' does not exist"));
        Assert.False(LoadFailure.IsWarning("CS5001: Program does not contain a static 'Main' method"));
    }
}
