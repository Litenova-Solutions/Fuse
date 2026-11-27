using System.Globalization;
using Fuse.Identity;
using Fuse.Operations;
using Fuse.Operations.Check;
using Fuse.Operations.Session;
using Fuse.Operations.Test;
namespace Fuse.Cli;

/// <summary>The test command, which runs the tests the working tree affects.</summary>
internal static class TestCliCommand
{
    /// <summary>Fuse's own options of <c>fuse test</c>: a near miss of one is refused, and every other token goes to <c>dotnet test</c>.</summary>
    internal static readonly string[] TestFuseOptions = ["--all", "--failed", "--clean", "--no-cache", "--max-failures", "--help"];

    /// <summary>
    ///     <c>fuse test</c>. <c>--all</c>, <c>--failed</c> and <c>dotnet test</c> arguments each choose the scope, so two
    ///     together are a usage failure rather than one of them quietly winning. A near miss of a Fuse option is refused;
    ///     every other token goes to <c>dotnet test</c>.
    /// </summary>
    internal static async Task<int> TestAsync(string[] args, CancellationToken cancellationToken)
    {
        // After the first -- every token goes to dotnet test unchanged, even one that reads as a Fuse option.
        var dash = Array.IndexOf(args, "--");
        string[] tail = dash < 0 ? [] : args[(dash + 1)..];
        args = dash < 0 ? args : args[..dash];

        // --session is the pre-shell hook's, which names the session whose command it rewrote; it is never passed on.
        string? session = null;
        var index = Array.IndexOf(args, "--session");
        if (index >= 0)
        {
            if (index + 1 >= args.Length)
            {
                Console.Error.WriteLine("fuse: --session takes the session's id");
                return 2;
            }

            session = args[index + 1];
            args = [.. args[..index], .. args[(index + 2)..]];
        }

        // With no --session, FUSE_SESSION in the environment names the session, as it does for fuse check.
        session ??= SessionOperation.EnvironmentSession;

        // --from-rewrite is the pre-shell hook's too: the command ran through Fuse, which the notice says.
        var fromRewrite = args.Contains("--from-rewrite");
        if (fromRewrite)
            args = [.. args.Where(a => a != "--from-rewrite")];

        var clean = false;
        var noCache = false;
        var maxFailures = TestOperation.DefaultMaxFailures;
        var remaining = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--clean")
            {
                clean = true;
                continue;
            }

            if (token == "--no-cache")
            {
                noCache = true;
                continue;
            }

            if (token == "--max-failures" || token.StartsWith("--max-failures=", StringComparison.Ordinal))
            {
                var value = token.StartsWith("--max-failures=", StringComparison.Ordinal)
                    ? token["--max-failures=".Length..]
                    : ++i < args.Length ? args[i] : null;
                if (value is null || !int.TryParse(value, CultureInfo.InvariantCulture, out maxFailures) || maxFailures < 1 || maxFailures > CheckArguments.MaxErrorsLimit)
                {
                    Console.Error.WriteLine($"fuse: --max-failures takes a count of 1 to {CheckArguments.MaxErrorsLimit}, not '{value ?? ""}'");
                    return 2;
                }

                continue;
            }

            remaining.Add(token);
        }

        var strippedArgs = remaining.ToArray();
        var all = strippedArgs.Contains("--all");
        var failed = strippedArgs.Contains("--failed");
        var head = strippedArgs.Where(a => a != "--all" && a != "--failed").ToList();
        var passthrough = head.Concat(tail).ToList();
        if (failed && (all || passthrough.Count > 0))
        {
            Console.Error.WriteLine("fuse: --failed reruns the tests that failed last time and cannot be combined with other arguments; pass the arguments without --failed to choose the scope");
            return 2;
        }

        if (all && passthrough.Count > 0)
        {
            // --all takes the same scope as a plain run: project, solution and directory paths with the build
            // switches. Any other argument chooses the tests itself and cannot be combined with it.
            if (TestScopeArguments.Parse(Environment.CurrentDirectory, passthrough) is not ParseResult<TestScopeArguments?>.Parsed parsedAll || parsedAll.Value is null)
            {
                Console.Error.WriteLine("fuse: --all runs every test and cannot be combined with other arguments; pass the arguments without --all to choose the scope");
                return 2;
            }
        }

        // Only what precedes -- can mistype a Fuse option; past it every token is dotnet test's.
        foreach (var token in head)
        {
            if (NearMiss.Suggest(token, TestFuseOptions) is { } suggestion)
            {
                Console.Error.WriteLine($"fuse: '{token}' is not a fuse test option; did you mean '{suggestion}'?");
                Console.Error.WriteLine(CommandUsage.Test);
                return 2;
            }
        }

        var root = CommandDispatcher.RequireRoot();
        if (root is null)
            return 2;
        if (fromRewrite)
            Console.Error.WriteLine("fuse: dotnet test routed to fuse test (tests affected by your changes; fuse test --all runs every test)");
        var invocation = new TestInvocation(root, Environment.CurrentDirectory, passthrough, all, session, Console.Out, failed, clean, noCache, ErrorLimit.FromCount(maxFailures));
        var result = await TestOperation.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        return CommandDispatcher.Print(result);
    }
}
