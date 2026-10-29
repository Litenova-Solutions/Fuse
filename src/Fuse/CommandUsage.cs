using Fuse.Harnesses;

namespace Fuse;

/// <summary>The usage block of every subcommand, and the top-level one. <c>--help</c> prints one to standard output and exits 0; a usage failure prints one to standard error and exits 2.</summary>
internal static class CommandUsage
{
    internal const string Top = """
        fuse - faster .NET build and test loop for AI coding agents

          fuse init [--instructions]  register Fuse's hooks with the agent harnesses this repository uses
                                    (--instructions: also add a Fuse section to AGENTS.md)
          fuse uninit               remove what fuse init wrote, stop this repository's engine and delete its state
          fuse check [files...] [--max-errors <n>|--all-errors] [--json]
                                    errors the working tree has that HEAD does not, across dependent projects
          fuse test [args...]       run the tests affected by your changes (with args: the tests those dotnet test arguments name)
          fuse test --all           run every test
          fuse test --failed        rerun the tests that failed in the last recorded run
          fuse build [args...] [--max-errors <n>|--all-errors] [--json] [--warnings]
                                    dotnet build, printing its errors
          fuse status               show the engine and the changed files
          fuse session begin <name> --paths <pattern> [<pattern>...]
                                    register an external writer as a session for its path patterns
          fuse session end <name>   end the registration
          fuse stop [--all]         stop this repository's engine (--all: every engine); the next command starts one
          fuse mcp                  stdio MCP server (fuse_check, fuse_test, fuse_build) for MCP hosts that run no hooks
          fuse hook <harness> <event>   the command registered hooks run
        """;

    internal const string Check = """
        fuse check [files...] [--max-errors <n>|--all-errors] [--json]
          errors the working tree has that HEAD does not, in the named files, or in every changed file when none
          are named, and in the files of dependent projects that use a changed declaration. A directory or a pattern
          with * or ? checks the changed sources under it and what they reach.
          --max-errors <n>  show at most n errors (default 20)
          --all-errors      show every error
          --json            one JSON object per error on stdout, then the summary line
        """;

    internal const string Test = """
        fuse test [dotnet test arguments] [--all|--failed]
          run the tests affected by your changes. With arguments that only say where the tests are, the affected
          tests in that scope; with any other argument, the tests those arguments name.
          --all     run every test (cannot be combined with other arguments)
          --failed  rerun the tests that failed in the last recorded run (cannot be combined with other arguments)
          Every other argument goes to dotnet test unchanged.
        """;

    internal const string Build = """
        fuse build [dotnet build arguments] [--max-errors <n>|--all-errors] [--json] [--warnings]
          dotnet build, printing its errors. Fuse adds -nologo -tl:off -v:q and a file log.
          Fuse options (not passed to dotnet build):
            --max-errors <n>  show at most n errors (default 20)
            --all-errors      show every error
            --json            one JSON object per error on stdout, then the summary line
            --warnings        list the warnings after the errors (default: count only)
          Every other argument goes to dotnet build unchanged.
        """;

    internal const string Stop = """
        fuse stop [--all]
          stop this repository's engine (--all: every engine); the next command starts one.
        """;

    internal const string Status = """
        fuse status
          show the engine and the changed files.
        """;

    internal const string Session = """
        fuse session begin <name> --paths <pattern> [<pattern>...] | fuse session end <name>
          register an external writer as a session for its path patterns, so a judge's stop leaves that writer's
          half-written errors to it; end the registration when the writer stops.
        """;

    internal const string Init = """
        fuse init [--instructions]
          register Fuse's hooks with the agent harnesses this repository uses
          (--instructions: also add a Fuse section to AGENTS.md)
        """;

    internal const string Uninit = """
        fuse uninit
          remove what fuse init wrote, stop this repository's engine and delete its state.
        """;

    internal const string Mcp = """
        fuse mcp
          stdio MCP server (fuse_check, fuse_test, fuse_build) for MCP hosts that run no hooks.
        """;

    internal const string Engine = """
        fuse engine <root>
          run the engine for the repository at <root>. The client starts it; you do not run this yourself.
        """;

    internal const string Version = """
        fuse --version
          print the version.
        """;

    /// <summary>The hook's usage, with the harnesses and events it takes, as <c>fuse hook</c> itself prints them.</summary>
    internal static string Hook =>
        $"""
        fuse hook <{string.Join('|', SupportedHarnesses.All.Select(h => h.Name))}> <{HookEvent.PostEdit}|{HookEvent.PreShell}|{HookEvent.Stop}>
          the command registered hooks run. It reads the harness's JSON from standard input.
        """;
}
