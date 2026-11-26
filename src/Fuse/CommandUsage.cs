namespace Fuse;

/// <summary>The usage block of every subcommand except the hook, whose usage the hook command prints itself, and the top-level one. <c>--help</c> prints one to standard output and exits 0; a usage failure prints one to standard error and exits 2, except <c>fuse init</c>, <c>fuse uninit</c> and <c>fuse session</c>, which print their own short usage line, and combined <c>fuse test</c> scopes, which print only their refusal.</summary>
internal static class CommandUsage
{
    internal const string Top = """
        fuse - faster .NET build and test loop for AI coding agents

          fuse init [--instructions]  register Fuse's hooks with the agent harnesses this repository uses
                                    (--instructions: also add a Fuse section to AGENTS.md)
          fuse uninit               remove what fuse init wrote, stop this repository's engine and delete its state
          fuse check [files...] [--max-errors <n>|--all-errors] [--json] [--brief]
                                    errors the working tree has that HEAD does not, across dependent projects
          fuse test [args...] [--clean] [--no-cache] [--max-failures <n>]
                                    run the tests affected by your changes (with args: the tests those dotnet test arguments name)
          fuse test --all           run every test
          fuse test --failed        rerun the tests that failed in the last recorded run
          fuse build [args...] [--max-errors <n>|--all-errors] [--json] [--warnings] [--changed] [--parallel-artifacts]
                                    dotnet build, printing its errors
          fuse gate [--session <name>] [--brief] [--test] [--max-failures <n>] [--json]
                                    run check (and optionally affected tests) as one deterministic worker gate
          fuse judge (--combine | --race) <patch-or-worktree...> [--json]
                                    evaluate candidate patches or worktrees in memory
          fuse graph [--json]       print the project DAG, coupling clusters, and test ownership map
          fuse impact <paths...> [--json]
                                    print projects, files, and test projects affected by the named paths
          fuse slim [--json]        ask the running engine to shed transient caches and compact memory
          fuse batch                process NDJSON EngineRequest lines on stdin over one persistent pipe
          fuse status [--json]      show the engine and the changed files
          fuse session begin <name> --paths <pattern> [<pattern>...]
                                    register an external writer as a session for its path patterns
          fuse session end <name>   end the registration
          fuse session guard <name> (--freeze <pattern>... | --unfreeze)
                                    freeze or unfreeze declaration changes under contract path patterns
          fuse watch (--session <name> | --all) [--json]
                                    stream check answers and guard violations as the engine produces them
          fuse stop [--all]         stop this repository's engine (--all: every engine); the next command starts one
          fuse mcp                  stdio MCP server (fuse_check, fuse_test, fuse_build) for MCP hosts that run no hooks
          fuse hook <harness> <event>   the command registered hooks run
        """;

    internal const string Check = """
        fuse check [files...] [--max-errors <n>|--all-errors] [--json] [--brief]
          errors the working tree has that HEAD does not, in the named files, or in every changed file when none
          are named, and in the files of dependent projects that use a changed declaration. A directory or a pattern
          with * or ? checks the changed sources under it and what they reach.
          --max-errors <n>  show at most n errors (default 20)
          --all-errors      show every error
          --json            one JSON object per error on stdout, then the summary line
          --brief           attach compact repair packets (source window, candidate members/overloads, usings, stubs)
        """;

    internal const string Test = """
        fuse test [dotnet test arguments] [--all|--failed] [--clean] [--no-cache] [--max-failures <n>]
          run the tests affected by your changes. With arguments that only say where the tests are, the affected
          tests in that scope; with any other argument, the tests those arguments name.
          --all               run every test (cannot be combined with other arguments)
          --failed            rerun the tests that failed in the last recorded run (cannot be combined with other arguments)
          --clean             bypass cached results; run fresh processes
          --no-cache          bypass cached test results
          --max-failures <n>  stop after n test failures (default: run every selected test)
          Every other argument goes to dotnet test unchanged.
        """;

    internal const string Build = """
        fuse build [dotnet build arguments] [--max-errors <n>|--all-errors] [--json] [--warnings] [--changed] [--parallel-artifacts]
          dotnet build, printing its errors. Fuse adds -nologo -tl:off -v:q and a file log.
          Fuse options (not passed to dotnet build):
            --max-errors <n>      show at most n errors (default 20)
            --all-errors          show every error
            --json                one JSON object per error on stdout, then the summary line
            --warnings            list the warnings after the errors (default: count only)
            --changed             build only the projects owning files changed since HEAD and their dependents
            --parallel-artifacts  isolate build output under Fuse's state directory so overlapping builds run concurrently
          Every other argument goes to dotnet build unchanged.
        """;

    internal const string Gate = """
        fuse gate [--session <name>] [--brief] [--test] [--max-failures <n>] [--json]
          run a single deterministic verification pass for a worker: checks introduced errors first, and when clean
          and --test is set, runs affected tests with fail-fast --max-failures. Exits 0 green, 1 errors/failures,
          2 refusal.
        """;

    internal const string Judge = """
        fuse judge (--combine | --race) <patch-or-worktree...> [--json]
          evaluate unified diff patches or git worktrees in memory before modifying disk or git.
          --combine           apply all patches onto one combined overlay and check it for introduced errors
          --race              evaluate each candidate independently and return a ranked scorecard
          --json              print the JudgeReport JSON object
        """;

    internal const string Graph = """
        fuse graph [--json]
          query the engine for the project DAG, coupling clusters, and test ownership map.
          --json  print the GraphReport JSON object
        """;

    internal const string Impact = """
        fuse impact <paths...> [--json]
          query the engine for owning projects, transitive dependents, candidate files, and affected test projects.
          --json  print the ImpactReport JSON object
        """;

    internal const string Slim = """
        fuse slim [--json]
          ask the running engine to release transient caches and run a compacting Gen2 GC without starting one.
          --json  print one JSON object with reclaimed and working-set memory
        """;

    internal const string Batch = """
        fuse batch
          read NDJSON EngineRequest lines on stdin and stream NDJSON EngineResponse lines on stdout over one
          persistent engine pipe connection.
        """;

    internal const string Stop = """
        fuse stop [--all]
          stop this repository's engine (--all: every engine); the next command starts one.
        """;

    internal const string Status = """
        fuse status [--json]
          show the engine, its memory, the changed files and the tree they make, and the limits past which checks
          slow down.
          --json  one JSON object with the engine, the tree and the limits
        """;

    internal const string Session = """
        fuse session begin <name> [--pid <pid>] --paths <pattern> [<pattern>...]
        fuse session begin --from <file>
        fuse session end <name>
        fuse session guard <name> --freeze <pattern> [<pattern>...]
        fuse session guard <name> --unfreeze
          register an external writer as a session for its path patterns, so a judge's stop leaves that writer's
          half-written errors to it; end the registration when the writer stops, or freeze contract patterns.
          --pid       end the registration on its own when that process exits
          --from      register every session a file names, one "<name><TAB><pattern>" per line
          --freeze    mark declaration surfaces under matching patterns as frozen for watch alerts
          --unfreeze  remove frozen contract patterns for the named guard
          A writer that runs fuse itself can set FUSE_SESSION=<name> instead: fuse check, fuse test and fuse mcp
          then answer as that session, and a check that names files credits them to it.
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
}
