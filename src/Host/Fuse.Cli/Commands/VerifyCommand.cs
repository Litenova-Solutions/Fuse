using System.Text;
using DotMake.CommandLine;
using Fuse.Workspace;

namespace Fuse.Cli.Commands;

/// <summary>
///     The G8 CI parity rehearsal command: <c>fuse verify --ci-parity</c> extracts the dotnet command sequence the
///     repository's GitHub Actions workflows run, prints it, optionally runs the rehearsable steps locally through
///     the same executor T0 uses (<c>--run</c>), and classifies the steps that cannot be rehearsed locally as
///     named classes (a secret, a package push). The contract is classification, not emulation: nothing is
///     silently skipped, so a "local green, CI red" surprise has a named reason here first.
/// </summary>
[CliCommand(
    Name = "verify",
    Description = "Verify against what CI will run. With --ci-parity: extract the workflows' dotnet command sequence, classify the steps that cannot be rehearsed locally (secrets, package push), and with --run execute the rehearsable ones through the local executor.",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class VerifyCommand
{
    private static readonly TimeSpan PerCommandTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The workspace directory. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>Rehearse the repository's CI dotnet steps (the current verify mode).</summary>
    [CliOption(Name = "--ci-parity", Description = "Rehearse the repository's CI dotnet steps and classify the non-rehearsable ones.")]
    public bool CiParity { get; set; }

    /// <summary>Also execute the rehearsable dotnet commands locally through the executor.</summary>
    [CliOption(Name = "--run", Description = "Execute the rehearsable dotnet commands locally (otherwise the sequence is only printed).")]
    public bool Run { get; set; }

    /// <summary>
    ///     Runs the verify command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the report has been printed.</returns>
    public async Task RunAsync(CliContext context)
    {
        if (!CiParity)
        {
            Console.Error.WriteLine("Specify a verify mode. Currently: --ci-parity (add --run to execute the rehearsable steps).");
            return;
        }

        var root = System.IO.Path.GetFullPath(Path);
        var report = await CiParityRehearser.RehearseAsync(root, Run, PerCommandTimeout, context.CancellationToken);

        var builder = new StringBuilder();
        if (report.Note is not null)
        {
            builder.AppendLine($"ci-parity: {report.Note}");
            Console.Out.WriteLine(builder.ToString().TrimEnd());
            return;
        }

        builder.AppendLine($"ci-parity: {report.WorkflowsScanned.Count} workflow(s) scanned ({string.Join(", ", report.WorkflowsScanned)})");
        builder.AppendLine();
        builder.AppendLine($"rehearsable dotnet steps ({report.RehearsableCommands.Count}), in CI order:");
        foreach (var c in report.RehearsableCommands)
            builder.AppendLine($"  $ {c}");

        builder.AppendLine();
        builder.AppendLine($"NOT rehearsable locally ({report.NonRehearsableSteps.Count}) - named, never silently skipped:");
        if (report.NonRehearsableSteps.Count == 0)
            builder.AppendLine("  (none)");
        foreach (var s in report.NonRehearsableSteps)
            builder.AppendLine($"  - {s}");

        if (Run)
        {
            builder.AppendLine();
            builder.AppendLine("execution results:");
            foreach (var r in report.ExecutionResults)
                builder.AppendLine($"  [{r.Status}] {r.Command}");
        }
        else
        {
            builder.AppendLine();
            builder.AppendLine("(pass --run to execute the rehearsable steps through the local executor)");
        }

        Console.Out.WriteLine(builder.ToString().TrimEnd());
    }
}
