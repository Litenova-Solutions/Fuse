using System.Text;
using Fuse.Client;
using Fuse.Dotnet.Build;
using Fuse.Dotnet.Processes;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
namespace Fuse.Operations.Build;

/// <summary>Builds the projects owning the files changed since HEAD and their dependents, or the whole solution when the selection cannot be trusted.</summary>
internal static class ChangedBuildRunner
{
    /// <summary>
    ///     Builds the projects owning the files changed since HEAD and their dependents, using one ephemeral MSBuild
    ///     traversal build when multiple projects are affected, under a DAG-aware closure lock. When nothing changed it
    ///     builds nothing; when the selection cannot be trusted it builds the whole solution instead.
    /// </summary>
    /// <param name="root">The repository to build the changed projects of.</param>
    /// <param name="workingDirectory">Where <c>dotnet build</c> runs.</param>
    /// <param name="arguments">The arguments for each <c>dotnet build</c>, unchanged.</param>
    /// <param name="options">How many errors to print, in what form, and whether the warnings are listed.</param>
    /// <param name="cancellationToken">Cancels the plan and the builds.</param>
    /// <param name="parallelArtifacts">True to isolate MSBuild output under the state directory via <c>--artifacts-path</c>.</param>
    internal static async Task<OperationResult> RunChangedAsync(RepoRoot root, string workingDirectory, IReadOnlyList<string> arguments, BuildOptions options, CancellationToken cancellationToken, bool parallelArtifacts = false)
    {
        var (filteredArgs, hasParallelFlag) = BuildOperation.StripParallelArtifactsFlag(arguments);
        parallelArtifacts |= hasParallelFlag;

        var response = await EngineClient.SendAsync(root, new EngineRequest.PlanChangedBuild(), TimeSpan.FromMinutes(10), cancellationToken, onBusy: BusyNotice.For()).ConfigureAwait(false);
        if (response is not EngineResponse.ChangedBuildAnswered { Plan: not null } answered)
            return OperationResult.Unanswered(response, "changed build plan");
        var plan = answered.Plan;
        if (plan.FallbackReason.Length > 0)
            return await FallbackAsync(root, workingDirectory, filteredArgs, options, plan.FallbackReason, cancellationToken, parallelArtifacts).ConfigureAwait(false);
        if (plan.BuildProjects.Length == 0)
        {
            if (plan.ChangedFileCount == 0)
                return new OperationResult(Outcome.Clean, $"fuse: no C# changes since HEAD; nothing to build (tree {plan.TreeHash})");
            return await FallbackAsync(root, workingDirectory, filteredArgs, options, $"{plan.ChangedFileCount} changed file(s) in no project", cancellationToken, parallelArtifacts).ConfigureAwait(false);
        }

        var names = plan.BuildProjects.Select(project => Path.GetFileNameWithoutExtension(project) ?? project).ToArray();
        var text = new StringBuilder();
        text.Append($"fuse: building {plan.BuildProjects.Length} project(s) for {plan.ChangedFileCount} changed file(s) (tree {plan.TreeHash}): {string.Join(", ", names)}");
        if (!new HashSet<string>(names).SetEquals(plan.ChangedInProjects))
            text.Append($"; changes in {string.Join(", ", plan.ChangedInProjects)}");
        if (plan.UnownedFileCount > 0)
            text.Append($"; {plan.UnownedFileCount} changed file(s) in no project");
        using var buildLock = BuildLock.AcquireForProjectsForClient(root, plan.BuildProjects, parallelArtifacts);
        var effectiveArgs = BuildOperation.WithArtifactsPath(filteredArgs, buildLock.ArtifactsPath);
        var stamp = await BuildResultsFile.StampForReuseAsync(root, cancellationToken).ConfigureAwait(false);
        if (stamp is not null && buildLock.ArtifactsPath is null && BuildOperation.ReusedBuild(root, stamp.Value, BuildResultsFile.ChangedScope, plan.BuildProjects, effectiveArgs, workingDirectory, options) is { } reused)
            return reused;

        await BuildOperation.YieldMemoryIfTightAsync(root, cancellationToken).ConfigureAwait(false);

        if (plan.BuildProjects.Length > 1)
        {
            var (result, log, seconds, rerunLine) = await BuildOperation.RunTraversalBuildAsync(
                root, plan.BuildProjects, [.. effectiveArgs, .. BuildOperation.QuietArguments], workingDirectory, root.StateDirectory, continueOnError: false, cancellationToken).ConfigureAwait(false);

            var treeStamp = await BuildOperation.StampAsync(root, cancellationToken).ConfigureAwait(false);
            var diagnostics = BuildOutputParser.Diagnostics(result.Output, root.Path);
            // A build server holding a locked file is shut down so the next build can copy; any other outcome leaves it running.
            if (result.ExitCode != 0 && diagnostics.Any(BuildOperation.NamesBuildServerLock))
                await SdkCapabilities.ShutdownMsBuildServerAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
            var warnings = log.Length == 0 ? [] : BuildLog.WarningsList(log, root.Path);
            var resultsPath = BuildResultsFile.WriteBuild(root, diagnostics, warnings, options.Json, treeStamp);
            var label = $"build ({plan.BuildProjects.Length} projects)";
            var runRecord = BuildOperation.BuildRun(label, result, log, seconds, diagnostics, warnings, restoreTarget: null);
            if (stamp is not null && buildLock.ArtifactsPath is null && BuildOperation.ShouldRecord(diagnostics))
                BuildResultsFile.WriteReuse(root, stamp.Value, BuildResultsFile.ChangedScope, plan.BuildProjects, effectiveArgs, BuildOperation.RelativeWorkingDirectory(root, workingDirectory), DateTime.UtcNow, [runRecord], treeStamp, diagnostics.Count);

            text.Append('\n');
            if (rerunLine is not null)
                text.Append(rerunLine).Append('\n');
            var rendered = BuildRenderer.Render(new BuildRenderContext(result, root.Path, seconds, label, options, resultsPath, log, treeStamp, diagnostics.Count, Passthrough: filteredArgs, Changed: true));
            text.Append(rendered.Text);
            return new OperationResult(rendered.Outcome, text.ToString());
        }

        var singleRuns = new List<(ProcessResult Result, string Log, double Seconds, string? RerunLine)>(plan.BuildProjects.Length);
        foreach (var project in plan.BuildProjects)
            singleRuns.Add(await BuildOperation.RunOneAsync(["build", project, .. effectiveArgs, .. BuildOperation.QuietArguments], workingDirectory, root.StateDirectory, cancellationToken).ConfigureAwait(false));

        // The results file holds every project at once; each summary names it with the combined count.
        var singleStamp = await BuildOperation.StampAsync(root, cancellationToken).ConfigureAwait(false);
        var parsed = singleRuns.Select((run, i) => BuildOperation.BuildRun($"build ({names[i]})", run.Result, run.Log, run.Seconds, BuildOutputParser.Diagnostics(run.Result.Output, root.Path), run.Log.Length == 0 ? [] : BuildLog.WarningsList(run.Log, root.Path), BuildOperation.RelativeProject(plan.BuildProjects[i], root.Path))).ToList();
        // A build server holding a locked file is shut down so the next build can copy; any other outcome leaves it running.
        if (parsed.Any(run => run.ExitCode != 0 && run.Errors.Any(BuildOperation.NamesBuildServerLock)))
            await SdkCapabilities.ShutdownMsBuildServerAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        var errors = parsed.SelectMany(run => run.Errors).ToList();
        var allWarnings = parsed.SelectMany(run => run.Warnings).ToList();

        var singleResultsPath = BuildResultsFile.WriteBuild(root, errors, allWarnings, options.Json, singleStamp);
        if (stamp is not null && buildLock.ArtifactsPath is null && BuildOperation.ShouldRecord(errors))
            BuildResultsFile.WriteReuse(root, stamp.Value, BuildResultsFile.ChangedScope, plan.BuildProjects, effectiveArgs, BuildOperation.RelativeWorkingDirectory(root, workingDirectory), DateTime.UtcNow, parsed, singleStamp, errors.Count);
        var failed = 0;
        foreach (var ((result, log, seconds, rerunLine), name, project) in singleRuns.Zip(names, plan.BuildProjects))
        {
            text.Append('\n');
            if (rerunLine is not null)
                text.Append(rerunLine).Append('\n');
            var rendered = BuildRenderer.Render(new BuildRenderContext(result, root.Path, seconds, $"build ({name})", options, singleResultsPath, log, singleStamp, errors.Count, RestoreTarget: BuildOperation.RelativeProject(project, root.Path), Passthrough: filteredArgs, Changed: true));
            if (rendered.Outcome == Outcome.ProblemsFound)
                failed++;
            text.Append(rendered.Text);
        }

        if (plan.BuildProjects.Length > 1)
            text.Append($"\nfuse: {failed} of {plan.BuildProjects.Length} project(s) failed");
        return new OperationResult(failed > 0 ? Outcome.ProblemsFound : Outcome.Clean, text.ToString());
    }

    /// <summary>Builds the whole solution because <paramref name="reason"/> says the changed projects cannot be selected, saying so first.</summary>
    private static async Task<OperationResult> FallbackAsync(RepoRoot root, string workingDirectory, IReadOnlyList<string> arguments, BuildOptions options, string reason, CancellationToken cancellationToken, bool parallelArtifacts = false)
    {
        var whole = await BuildOperation.RunAsync(root, workingDirectory, arguments, options, cancellationToken, includeTreeStamp: true, parallelArtifacts: parallelArtifacts).ConfigureAwait(false);
        return whole with { Text = $"fuse: {reason}; building the whole solution instead\n" + whole.Text };
    }
}
