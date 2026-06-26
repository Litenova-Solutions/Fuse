using System.ComponentModel;
using Fuse.Fusion.Scoping;
using Fuse.Collection.Models;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Reduction.Security;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     MCP tool definitions for Fuse, exposed to AI agents through the Model Context Protocol server.
/// </summary>
/// <remarks>
///     Each method maps to an MCP tool whose name is set by <see cref="McpServerToolAttribute" /> (for example
///     <c>fuse_skeleton</c>). Every parameter maps to an MCP tool argument the agent supplies; the parameter
///     <c>[Description]</c> attributes are the agent-facing schema descriptions. All tools are read-only, run
///     fusion in memory (no files are written), and return errors as descriptive strings rather than throwing.
/// </remarks>
[McpServerToolType]
public sealed partial class FuseTools
{
    /// <summary>
    ///     Answers a task by choosing a scoping strategy (skeleton, focus, or search) from the task text and a
    ///     token budget, then packing the result to that budget. Collapses the manual survey-then-scope loop
    ///     into one call.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="path">Absolute or relative path to the source directory.</param>
    /// <param name="task">A natural-language description of what the agent needs to do or find.</param>
    /// <param name="tokenBudget">The maximum number of tokens the returned context may use.</param>
    /// <param name="excludeDirectories">Directory names to skip, or <see langword="null" /> for none.</param>
    /// <param name="excludeFiles">File names to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludePatterns">Glob patterns to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludeTestProjects">When <see langword="true" />, skip all test project directories.</param>
    /// <param name="cancellationToken">Token used to cancel the fusion run.</param>
    /// <returns>
    ///     The packed context, prefixed with a one-line note naming the chosen strategy, or a descriptive error
    ///     message when the directory is missing or fusion fails.
    /// </returns>
    [McpServerTool(Name = "fuse_ask", ReadOnly = true)]
    [Description("Give a task and a token budget. Fuse picks the scoping strategy (skeleton for broad questions, focus for a named type, search otherwise) and packs the context to the budget. One call instead of survey-then-scope.")]
    public static async Task<string> FuseAskAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [Description("Absolute or relative path to the source directory.")] string path,
        [Description("What you need to do or find, in natural language.")] string task,
        [Description("Maximum tokens the returned context may use.")] int tokenBudget = 20000,
        [Description("Directory names to skip.")] string[]? excludeDirectories = null,
        [Description("File names to exclude.")] string[]? excludeFiles = null,
        [Description("Glob patterns to exclude.")] string[]? excludePatterns = null,
        [Description("Exclude all test project directories.")] bool excludeTestProjects = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(task))
            return "Error: task must be a non-empty description of what you need.";

        var budget = tokenBudget > 0 ? tokenBudget : 20000;
        var plan = AskStrategySelector.Select(task, budget);

        // Focus can fail when the named type is not in the collected set; fall back to search so a wrong guess
        // degrades into a broader (still budgeted) result rather than an error.
        var result = await RunAskPlanAsync(
            orchestrator, templateRegistry, path, task, budget, plan,
            excludeDirectories, excludeFiles, excludePatterns, excludeTestProjects, cancellationToken);

        if (plan.Mode == AskMode.Focus && result.IsRecoverable)
        {
            plan = new AskPlan(AskMode.Search, null, plan.Depth);
            result = await RunAskPlanAsync(
                orchestrator, templateRegistry, path, task, budget, plan,
                excludeDirectories, excludeFiles, excludePatterns, excludeTestProjects, cancellationToken);
        }

        var note = $"<!-- fuse_ask: strategy={plan.Mode.ToString().ToLowerInvariant()}" +
                   (plan.Seed is not null ? $" seed=\"{plan.Seed}\"" : string.Empty) +
                   $" budget={budget} -->\n";
        return result.IsSuccess ? note + result.Content : result.Content;
    }

    private static Task<ToolResult> RunAskPlanAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        string path,
        string task,
        int budget,
        AskPlan plan,
        string[]? excludeDirectories,
        string[]? excludeFiles,
        string[]? excludePatterns,
        bool excludeTestProjects,
        CancellationToken cancellationToken) =>
        FuseToolHelpers.ExecuteDotNetResultAsync(
            orchestrator,
            templateRegistry,
            path,
            builder =>
            {
                builder.WithEmissionOptions(new EmissionOptions
                {
                    MaxTokens = budget,
                    ShowTokenCount = false,
                    IncludeManifest = true,
                });

                builder.WithReductionOptions(new ReductionOptions(
                    level: plan.Mode == AskMode.Skeleton ? ReductionLevel.Skeleton : ReductionLevel.Aggressive,
                    enableRedaction: true));

                if (plan.Mode == AskMode.Focus && plan.Seed is not null)
                    builder.WithFocusOptions(new FocusOptions(plan.Seed, plan.Depth));
                else if (plan.Mode == AskMode.Search)
                    builder.WithQueryOptions(new QueryOptions(task, 10, plan.Depth));

                FuseToolHelpers.ApplyCommonFilters(
                    builder,
                    null, null, null,
                    excludeDirectories, excludeFiles, excludePatterns,
                    excludeTestProjects: excludeTestProjects);
            },
            trackTopTokenFiles: false,
            cancellationToken);
}
