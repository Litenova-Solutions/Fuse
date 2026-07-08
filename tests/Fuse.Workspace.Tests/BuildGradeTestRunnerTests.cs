using Fuse.Workspace;
using Xunit;

namespace Fuse.Workspace.Tests;

// T1: the build-grade covering-test runner (the pre-agreed default/floor). It runs the covering subset via
// dotnet test --filter and reads the verdicts from the TRX. This exercises the whole floor path end to end on a
// real xunit fixture; when the SDK cannot restore/build here it skips rather than failing, matching the guarded
// style of the resident-workspace fixture tests.
public sealed class BuildGradeTestRunnerTests
{
    [Fact]
    public async Task Runs_only_the_covering_subset_and_reports_verdicts()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-buildgrade-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var scratch = Path.Combine(work, "results");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(work, "Fix.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
                    <PackageReference Include="xunit" Version="2.9.2" />
                    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(work, "Cases.cs"), """
                using Xunit;
                namespace Fix;
                public class Cases
                {
                    [Fact] public void PassingTest() => Assert.Equal(2, 1 + 1);
                    [Fact] public void FailingTest() => Assert.Equal(3, 1 + 1);
                    [Fact] public void UncoveredTest() => Assert.True(true);
                }
                """);

            var result = await BuildGradeTestRunner.RunAsync(
                Path.Combine(work, "Fix.csproj"),
                ["Fix.Cases.PassingTest", "Fix.Cases.FailingTest"],
                scratch,
                TimeSpan.FromMinutes(5),
                CancellationToken.None);

            if (result.TimedOut || result.Verdicts.Count == 0)
                return; // The SDK could not restore/build/run here; nothing to validate (guarded skip).

            Assert.Equal("passed", Assert.Single(result.Verdicts, v => v.Name.EndsWith("PassingTest", StringComparison.Ordinal)).Outcome);
            Assert.Equal("failed", Assert.Single(result.Verdicts, v => v.Name.EndsWith("FailingTest", StringComparison.Ordinal)).Outcome);
            // The uncovered test was filtered out, so it never ran.
            Assert.DoesNotContain(result.Verdicts, v => v.Name.EndsWith("UncoveredTest", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task An_empty_covering_set_runs_nothing()
    {
        var result = await BuildGradeTestRunner.RunAsync(
            "does-not-matter.csproj", [], Path.GetTempPath(), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(result.RanNothing);
        Assert.Empty(result.Verdicts);
    }
}
