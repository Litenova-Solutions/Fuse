using Fuse.Operations;

namespace Fuse.Tests.Unit;

/// <summary>
///     How `fuse test` spells a positional project or solution path for `dotnet test`, which refuses the positional
///     form on Microsoft.Testing.Platform. A directory stays positional, which `dotnet test` reads as the project it holds.
/// </summary>
public class TestArgumentsTests
{
    [Fact]
    public void A_positional_project_or_solution_is_named_with_its_switch()
    {
        Assert.Equal(
            ["--project", "tests/Lib.Tests/Lib.Tests.csproj", "--no-restore"],
            TestOperation.NormalizeArguments(Path.GetTempPath(), ["tests/Lib.Tests/Lib.Tests.csproj", "--no-restore"]));
        Assert.Equal(
            ["--solution", "NightHall.slnx"],
            TestOperation.NormalizeArguments(Path.GetTempPath(), ["NightHall.slnx"]));
    }

    [Fact]
    public void A_positional_directory_is_a_project_on_the_platform_and_stays_positional_on_vstest()
    {
        var root = Directory.CreateTempSubdirectory("fuse-test-args");
        try
        {
            var tests = root.CreateSubdirectory("Lib.Tests");

            Assert.Equal(
                ["--project", "Lib.Tests"],
                TestOperation.NormalizeArguments(root.FullName, ["Lib.Tests"]));
            Assert.Equal(
                ["--project", tests.FullName, "--no-build"],
                TestOperation.NormalizeArguments(root.FullName, [tests.FullName, "--no-build"]));
            Assert.Equal(
                ["Lib.Tests"],
                TestOperation.NormalizeArguments(root.FullName, ["Lib.Tests"], testingPlatform: false));
            Assert.Equal(
                ["Lib.Tests/Lib.Tests.csproj", "--no-build"],
                TestOperation.NormalizeArguments(root.FullName, ["Lib.Tests/Lib.Tests.csproj", "--no-build"], testingPlatform: false));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Assemblies_switches_and_an_existing_project_switch_pass_unchanged()
    {
        Assert.Equal(["Lib.Tests.dll"], TestOperation.NormalizeArguments(Path.GetTempPath(), ["Lib.Tests.dll"]));
        Assert.Equal(["--filter-method", "*Name"], TestOperation.NormalizeArguments(Path.GetTempPath(), ["--filter-method", "*Name"]));
        Assert.Equal(
            ["--project", "Lib.Tests.csproj", "extra"],
            TestOperation.NormalizeArguments(Path.GetTempPath(), ["--project", "Lib.Tests.csproj", "extra"]));
        Assert.Equal([], TestOperation.NormalizeArguments(Path.GetTempPath(), []));
        Assert.Equal(["no-such-folder"], TestOperation.NormalizeArguments(Path.GetTempPath(), ["no-such-folder"]));
    }

    [Fact]
    public void A_relative_results_directory_is_made_absolute()
    {
        var root = Path.GetTempPath();

        Assert.Equal(
            ["--project", "Lib.Tests.csproj", "--results-directory", Path.GetFullPath("out", root)],
            TestOperation.NormalizeArguments(root, ["Lib.Tests.csproj", "--results-directory", "out"]));
        Assert.Equal(
            [$"--results-directory={Path.GetFullPath("out", root)}"],
            TestOperation.NormalizeArguments(root, ["--results-directory=out"]));
    }

    [Fact]
    public void A_posix_results_directory_becomes_a_windows_path_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.Equal(
            ["--results-directory", @"C:\Users\me\scratch"],
            TestOperation.NormalizeArguments(@"C:\repo", ["--results-directory", "/c/Users/me/scratch"]));
    }
}
