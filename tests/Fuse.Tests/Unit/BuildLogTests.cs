using Fuse.Dotnet;
using Fuse.Operations;

namespace Fuse.Tests.Unit;

/// <summary>
///     The file log Fuse adds to a build: what a command that exited with a code other than 0 printed, read from the lines
///     its node logged after the command's echo, and where the build's time went, from MSBuild's performance summary.
/// </summary>
public class BuildLogTests
{
    // The shape MSBuild's file logger writes at normal verbosity on several nodes: a node number switches the node, and
    // the lines after it without one belong to the same node.
    private const string Log = """
        Build started 17/10/2026 13:00:00.
             1>Project "C:\repo\Api\Api.csproj" on node 1 (default targets).
             2>Project "C:\repo\Lib\Lib.csproj" on node 2 (default targets).
             1>_GenerateOpenApiDocument:
                 dotnet "C:\tools\dotnet-getdocument.dll" --assembly Api.dll
                 Generating document named 'v1'.
             2>CoreCompile:
                 csc.exe /noconfig Lib.cs
             1>_GenerateOpenApiDocument:
                 System.InvalidOperationException: Duplicate audit action 'Create' on AuditController.
                    at Api.Audit.Register(String action) in C:\repo\Api\Audit.cs:line 12
             1>C:\repo\Api\Api.csproj(40,5): error MSB3073: The command "dotnet "C:\tools\dotnet-getdocument.dll" --assembly Api.dll" exited with code 11. [C:\repo\Api\Api.csproj]
             1>Done Building Project "C:\repo\Api\Api.csproj" (default targets) -- FAILED.

        Project Performance Summary:
              919000 ms  C:\repo\Api\Api.csproj   3 calls
                    900000 ms  Build                                      1 calls
               12000 ms  C:\repo\Lib\Lib.csproj   2 calls
              640000 ms  C:\repo\Tests\Tests.csproj   1 calls
               30000 ms  C:\repo\All.slnx.metaproj   1 calls

        Task Performance Summary:
                5 ms  Message                                    3 calls
           640200 ms  Csc                                        4 calls
           120000 ms  Exec                                       1 calls
            12300 ms  RestoreTask                                1 calls

        Build FAILED.

               "C:\repo\Api\Api.csproj" (default target) (1) ->
                 C:\repo\Api\Api.csproj(40,5): error MSB3073: The command "dotnet "C:\tools\dotnet-getdocument.dll" --assembly Api.dll" exited with code 11. [C:\repo\Api\Api.csproj]
        """;

    [Fact]
    public void A_failed_command_keeps_what_its_node_printed_and_not_what_other_nodes_did()
    {
        var (command, lines) = Assert.Single(BuildLog.FailedCommands(Log));

        Assert.Equal("dotnet \"C:\\tools\\dotnet-getdocument.dll\" --assembly Api.dll", command);
        Assert.Equal(
            [
                "Generating document named 'v1'.",
                "_GenerateOpenApiDocument:",
                "System.InvalidOperationException: Duplicate audit action 'Create' on AuditController.",
                @"at Api.Audit.Register(String action) in C:\repo\Api\Audit.cs:line 12",
            ],
            lines);
    }

    [Fact]
    public void Times_name_the_slowest_tasks_and_projects()
    {
        Assert.Equal(
            "slowest tasks summed over projects: Csc 640.2 s, Exec 120.0 s, RestoreTask 12.3 s; slowest projects: Api 919.0 s, Tests 640.0 s, Lib 12.0 s",
            BuildLog.Times(Log));
    }

    [Fact]
    public void A_log_without_a_summary_has_no_times_and_no_commands()
    {
        Assert.Null(BuildLog.Times("Build succeeded.\n"));
        Assert.Empty(BuildLog.FailedCommands("Build succeeded.\n"));
    }

    [Fact]
    public void The_answer_prints_what_the_failed_command_printed_under_its_error_and_the_times_of_a_long_build()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";
        var output = """C:\repo\Api\Api.csproj(40,5): error MSB3073: The command "dotnet "C:\tools\dotnet-getdocument.dll" --assembly Api.dll" exited with code 11. [C:\repo\Api\Api.csproj]""";

        var answer = BuildOperation.Render(new ProcessResult(1, output), root, 919, "build", Log);

        var lines = answer.Text.Split('\n');
        var error = Array.FindIndex(lines, l => l.Contains("error MSB3073", StringComparison.Ordinal));
        Assert.Equal("  System.InvalidOperationException: Duplicate audit action 'Create' on AuditController.", lines[error + 3]);
        Assert.StartsWith("fuse: build failed with 1 error(s) in 919.0 s (slowest tasks summed over projects: Csc 640.2 s", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_short_build_prints_no_times()
    {
        var answer = BuildOperation.Render(new ProcessResult(0, ""), "/repo", BuildOperation.TimesFromSeconds - 0.1, "build", Log);

        Assert.Equal($"fuse: build succeeded in {BuildOperation.TimesFromSeconds - 0.1:0.0} s", answer.Text);
    }

    [Fact]
    public void A_source_file_the_compiler_could_not_find_is_read_from_its_error()
    {
        var path = OperatingSystem.IsWindows() ? @"C:\repo\Lib\Moved.cs" : "/repo/Lib/Moved.cs";
        var output = $"CSC : error CS2001: Source file '{path}' could not be found. [/repo/Lib/Lib.csproj]\nCSC : error CS1234: other";

        Assert.Equal([path], BuildOperation.MovedSources(output));
    }
}
