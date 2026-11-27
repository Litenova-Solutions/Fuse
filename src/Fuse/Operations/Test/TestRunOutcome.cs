using Fuse.Dotnet.Processes;
using Fuse.Dotnet.Test;
namespace Fuse.Operations.Test;

/// <summary>
///     What one test run produced: its counts when they were read, or the process without them, with the
///     build log and where its files went. A Microsoft.Testing.Platform run that exited with 0 carries no
///     process, because there is nothing to explain.
/// </summary>
/// <param name="Outcome">The run's counts, or null when the run ended without results.</param>
/// <param name="Process">The test process when it ended without results, or null when the run has counts.</param>
/// <param name="Log">The build's file log for a VSTest run that builds with MSBuild, or empty.</param>
/// <param name="ResultsDirectory">Where the run wrote its result files.</param>
/// <param name="TimedOutFramework">The target framework a timed-out run hung on, or null.</param>
internal sealed record TestRunOutcome(TestRunSummary? Outcome, ProcessResult? Process, string Log, string ResultsDirectory, string? TimedOutFramework);
