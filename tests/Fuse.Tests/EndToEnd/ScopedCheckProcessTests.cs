using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

/// <summary>
///     <c>fuse check</c> scoped to a directory or a glob reports the slice and what it reaches, and refuses a scope
///     that matches nothing checkable.
/// </summary>
public class ScopedCheckProcessTests
{
    [Fact]
    public async Task Check_of_a_directory_reports_the_slice_and_what_it_reaches()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");

            var scoped = await FuseProcess.RunAsync(repo.Path, null, "check", "Lib/");
            Assert.Equal(1, scoped.ExitCode);
            Assert.Contains("App/Program.cs", scoped.Stdout, StringComparison.Ordinal);
            Assert.Contains("checked Lib and what it reaches", scoped.Stdout, StringComparison.Ordinal);

            var glob = await FuseProcess.RunAsync(repo.Path, null, "check", "Lib/**/*.cs");
            Assert.Equal(1, glob.ExitCode);
            Assert.Contains("App/Program.cs", glob.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task Check_of_a_clean_slice_reports_clean_for_the_slice()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");

            var scoped = await FuseProcess.RunAsync(repo.Path, null, "check", "Lib/");
            Assert.Equal(0, scoped.ExitCode);
            Assert.Contains("no errors introduced in Lib and what it reaches", scoped.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task Check_of_a_scope_matching_nothing_is_refused()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");

            var refused = await FuseProcess.RunAsync(repo.Path, null, "check", "docs/**");
            Assert.Equal(2, refused.ExitCode);
            Assert.Contains("matches no changed C# source file", refused.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }
}
