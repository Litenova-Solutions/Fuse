using Fuse.Operations;
using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

/// <summary>
///     A test application that never exits: the run kills its process tree once it writes no output for the
///     inactivity timeout, and answers that the run timed out, never that it passed.
/// </summary>
public class HangingTestRunTests
{
    [Fact]
    public async Task A_hanging_test_run_is_killed_and_reported_as_timed_out()
    {
        using var repo = CreateHangingRepo();
        var project = repo.Full("Hang.Tests/Hang.Tests.csproj");
        var run = new TestRun(project, "Hang.Tests", new TestRunMode.Build(), null, false, null, [], []);
        var scope = new TestScopeArguments([project], ["-f", "net10.0"], false, null, "net10.0");

        var started = Environment.TickCount64;
        var result = await TestOperation.RunPlanAsync(
            repo.Root, new TestPlan([run], 1, 1, "ran 1 test(s)"), started, TestContext.Current.CancellationToken,
            scope: scope, inactivityTimeout: TimeSpan.FromSeconds(15));

        // The hanging test sleeps three minutes; the timeout kills the run long before it would finish.
        Assert.True(Environment.TickCount64 - started < 150_000, $"the run took {(Environment.TickCount64 - started) / 1000} s");
        Assert.Equal(Outcome.ProblemsFound, result.Outcome);
        Assert.Contains("the test run of Hang.Tests (net10.0) timed out after 15 seconds without output", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hanging_test_run_with_a_failure_cap_is_killed_as_timed_out()
    {
        using var repo = CreateHangingRepo();
        var started = Environment.TickCount64;

        var (process, _) = await TestOperation.RunWithEarlyStopAsync(
            "dotnet", ["test", repo.Full("Hang.Tests/Hang.Tests.csproj"), "--no-build", "--no-restore", "-nologo", "-tl:off"],
            repo.Root.Path, 1, TestContext.Current.CancellationToken, TimeSpan.FromSeconds(15));

        Assert.True(Environment.TickCount64 - started < 150_000, "the hanging test application was not killed");
        Assert.True(process.TimedOut);
        Assert.Contains("Hang.Tests", process.Output, StringComparison.Ordinal);
    }

    private static FixtureRepo CreateHangingRepo()
    {
        var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            [".gitignore"] = "bin/\nobj/\n",
            ["Hang.Tests/Hang.Tests.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsPackable>false</IsPackable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                    <PackageReference Include="xunit" Version="2.9.3" />
                    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                  </ItemGroup>
                  <ItemGroup>
                    <Using Include="Xunit" />
                  </ItemGroup>
                </Project>
                """,
            ["Hang.Tests/HangTests.cs"] = """
                namespace Hang.Tests;

                public class HangTests
                {
                    [Fact]
                    public void Hangs() => Thread.Sleep(TimeSpan.FromMinutes(3));
                }
                """,
        });
        FixtureRepo.Run(repo.Root.Path, "dotnet", "build", "Hang.Tests/Hang.Tests.csproj", "-nologo", "-v:q");
        return repo;
    }
}
