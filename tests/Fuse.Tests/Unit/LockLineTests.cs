using Fuse.Dotnet;

namespace Fuse.Tests.Unit;

public class LockLineTests
{
    private static BuildDiagnostic CopyError(string message) =>
        new("App/App.csproj", null, null, "MSB3027", message);

    [Fact]
    public void A_lock_naming_its_holder_names_the_file_the_process_and_what_to_do()
    {
        var error = CopyError("""Could not copy "Lib/bin/Debug/Lib.dll" to "App/bin/Debug/Lib.dll". Exceeded retry count of 10. Failed. The file is locked by: "NightHall.WebApi (4212)".""");

        Assert.Equal(
            "locked: App/bin/Debug/Lib.dll is held by NightHall.WebApi (pid 4212); stop that process, then run the build again",
            LockLine.For(error, "/repo"));
    }

    [Fact]
    public void A_lock_held_by_a_build_server_says_to_shut_it_down()
    {
        var error = CopyError("""Could not copy "a.dll" to "b.dll". The file is locked by: "dotnet (8120)".""");

        Assert.Equal(
            "locked: b.dll is held by dotnet (pid 8120), a build server; run dotnet build-server shutdown, then run the build again",
            LockLine.For(error, "/repo"));
    }

    [Theory]
    [InlineData("MSB3021")]
    [InlineData("MSB3026")]
    [InlineData("MSB3027")]
    public void A_lock_naming_no_holder_gets_the_generic_line(string code)
    {
        var error = new BuildDiagnostic("App/App.csproj", null, null, code, "Could not copy \"a.dll\" to \"b.dll\". The process cannot access the file 'b.dll' because it is being used by another process.");

        Assert.Equal(
            "locked: b.dll is being used by another process; stop the running application or build that holds it, then run the build again",
            LockLine.For(error, "/repo"));
    }

    [Fact]
    public void An_error_that_is_no_locked_file_gets_no_line()
    {
        Assert.Null(LockLine.For(new BuildDiagnostic("Lib/Calc.cs", 1, 1, "CS1002", "; expected"), "/repo"));
        Assert.Null(LockLine.For(CopyError("Could not copy \"a.dll\" to \"b.dll\". Exceeded retry count of 10. Failed."), "/repo"));
        Assert.Null(LockLine.For(new BuildDiagnostic("MSBUILD", null, null, "MSB1009", "Project file does not exist and is locked by: \"x (1)\"."), "/repo"));
    }
}
