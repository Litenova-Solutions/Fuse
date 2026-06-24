using System.ComponentModel;
using Fuse.Fusion.Scoping;
using Fuse.Collection.Models;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Plugins.Abstractions.Options;
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
public sealed class FuseTools
{
    /// <summary>
    ///     Emits a table of contents for a .NET codebase: a directory tree with per-file token costs and a
    ///     symbol outline. The cheapest first call for surveying an unfamiliar codebase.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="path">Absolute or relative path to the source directory.</param>
    /// <param name="excludeDirectories">Directory names to skip, or <see langword="null" /> for none.</param>
    /// <param name="excludeFiles">File names to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludePatterns">Glob patterns to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludeTestProjects">When <see langword="true" />, skip all test project directories.</param>
    /// <param name="maxTokens">
    ///     The largest table of contents to return. On a codebase whose full map would exceed this, the document
    ///     degrades (drops symbol outlines, then collapses to directory aggregates) so the result stays inline
    ///     rather than overflowing the tool-result size cap.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the fusion run.</param>
    /// <returns>
    ///     The table-of-contents document as a single string, or a descriptive error message when the directory
    ///     is missing or fusion fails.
    /// </returns>
    [McpServerTool(Name = "fuse_toc", ReadOnly = true)]
    [Description("Emits a table of contents (directory tree, symbol outline, and per-file token costs) for a .NET codebase. The cheapest first call when exploring: prefer this over grepping or opening files blindly to survey the codebase, then fetch specific files with fuse_focus or fuse_search. On a large codebase the map degrades to fit maxTokens (drops outlines, then collapses to directories) instead of overflowing.")]
    public static Task<string> FuseTocAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [Description("Absolute or relative path to the source directory.")] string path,
        [Description("Directory names to skip.")] string[]? excludeDirectories = null,
        [Description("File names to exclude.")] string[]? excludeFiles = null,
        [Description("Glob patterns to exclude.")] string[]? excludePatterns = null,
        [Description("Exclude all test project directories.")] bool excludeTestProjects = false,
        [Description("Largest table of contents to return, in tokens. The map degrades to fit rather than overflow. Defaults to 24000.")] int maxTokens = 24000,
        CancellationToken cancellationToken = default) =>
        FuseToolHelpers.ExecuteDotNetAsync(
            orchestrator,
            templateRegistry,
            path,
            builder =>
            {
                builder
                    .WithEmissionOptions(new EmissionOptions
                    {
                        ShowTokenCount = false,
                        IncludeManifest = false,
                        TableOfContents = true,
                        TableOfContentsMaxTokens = maxTokens > 0 ? maxTokens : 24000,
                    })
                    .WithReductionOptions(new ReductionOptions(enableRedaction: true));

                FuseToolHelpers.ApplyCommonFilters(
                    builder,
                    null, null, null,
                    excludeDirectories, excludeFiles, excludePatterns,
                    excludeTestProjects: excludeTestProjects);
            },
            trackTopTokenFiles: false,
            cancellationToken);

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

        if (plan.Mode == AskMode.Focus && result.StartsWith("Error", StringComparison.Ordinal))
        {
            plan = new AskPlan(AskMode.Search, null, plan.Depth);
            result = await RunAskPlanAsync(
                orchestrator, templateRegistry, path, task, budget, plan,
                excludeDirectories, excludeFiles, excludePatterns, excludeTestProjects, cancellationToken);
        }

        var note = $"<!-- fuse_ask: strategy={plan.Mode.ToString().ToLowerInvariant()}" +
                   (plan.Seed is not null ? $" seed=\"{plan.Seed}\"" : string.Empty) +
                   $" budget={budget} -->\n";
        return result.StartsWith("Error", StringComparison.Ordinal) ? result : note + result;
    }

    private static Task<string> RunAskPlanAsync(
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
        FuseToolHelpers.ExecuteDotNetAsync(
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

    /// <summary>
    ///     Emits a structural skeleton of a .NET codebase for low-token architecture review.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="path">Absolute or relative path to the source directory.</param>
    /// <param name="excludeDirectories">Directory names to skip, or <see langword="null" /> for none.</param>
    /// <param name="excludeFiles">File names to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludePatterns">Glob patterns to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludeTestProjects">When <see langword="true" />, skip all test project directories.</param>
    /// <param name="semanticMarkers">When <see langword="true" />, prepend structural annotation comments to each entry.</param>
    /// <param name="publicApi">When <see langword="true" />, emit only public and protected member skeletons.</param>
    /// <param name="maxTokens">Hard token limit at which emission stops, or <see langword="null" /> for unlimited.</param>
    /// <param name="trackTopTokenFiles">When <see langword="true" />, append the top token-consuming files to the trailing stats comment.</param>
    /// <param name="cancellationToken">Token used to cancel the fusion run.</param>
    /// <returns>
    ///     The skeleton fusion output (signatures without bodies) as a single string, or a descriptive error
    ///     message when the directory is missing or fusion fails.
    /// </returns>
    [McpServerTool(Name = "fuse_skeleton", ReadOnly = true)]
    [Description("Emits structural skeleton only (signatures, no bodies) for a .NET codebase. Use for cold-start architecture review.")]
    public static Task<string> FuseSkeletonAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [Description("Absolute or relative path to the source directory.")] string path,
        [Description("Directory names to skip.")] string[]? excludeDirectories = null,
        [Description("File names to exclude.")] string[]? excludeFiles = null,
        [Description("Glob patterns to exclude.")] string[]? excludePatterns = null,
        [Description("Exclude all test project directories.")] bool excludeTestProjects = false,
        [Description("Prepend structural annotation comments.")] bool semanticMarkers = false,
        [Description("Emit only public and protected member skeletons.")] bool publicApi = false,
        [Description("Hard token limit.")] int? maxTokens = null,
        [Description("Include top token-consuming files in the stats comment.")] bool trackTopTokenFiles = false,
        CancellationToken cancellationToken = default) =>
        FuseToolHelpers.ExecuteDotNetAsync(
            orchestrator,
            templateRegistry,
            path,
            builder =>
            {
                builder
                    .WithEmissionOptions(new EmissionOptions
                    {
                        MaxTokens = maxTokens,
                        ShowTokenCount = false,
                        TrackTopTokenFiles = trackTopTokenFiles,
                        IncludeManifest = true
                    })
                    .WithReductionOptions(new ReductionOptions(
                        level: publicApi ? ReductionLevel.PublicApi : ReductionLevel.Skeleton,
                        includeSemanticMarkers: semanticMarkers,
                        enableRedaction: true));

                FuseToolHelpers.ApplyCommonFilters(
                    builder,
                    null, null, null,
                    excludeDirectories, excludeFiles, excludePatterns,
                    excludeTestProjects: excludeTestProjects);
            },
            trackTopTokenFiles,
            cancellationToken);

    /// <summary>
    ///     Scopes fusion to a type, file, or directory and its dependencies.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="path">Absolute or relative path to the source directory.</param>
    /// <param name="focus">Type name, filename, or path used as the focus seed.</param>
    /// <param name="depth">Number of dependency hops to traverse out from the seed.</param>
    /// <param name="excludeDirectories">Directory names to skip, or <see langword="null" /> for none.</param>
    /// <param name="excludeFiles">File names to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludePatterns">Glob patterns to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludeTestProjects">When <see langword="true" />, skip all test project directories.</param>
    /// <param name="level">The C# reduction level (none, standard, aggressive, skeleton, publicApi).</param>
    /// <param name="maxTokens">Hard token limit at which emission stops, or <see langword="null" /> for unlimited.</param>
    /// <param name="trackTopTokenFiles">When <see langword="true" />, append the top token-consuming files to the trailing stats comment.</param>
    /// <param name="session">Session id for session-delta emission, or <see langword="null" /> to disable it.</param>
    /// <param name="cancellationToken">Token used to cancel the fusion run.</param>
    /// <returns>
    ///     The focus-scoped fusion output as a single string, or a descriptive error message when the directory
    ///     is missing or fusion fails.
    /// </returns>
    [McpServerTool(Name = "fuse_focus", ReadOnly = true)]
    [Description("Scopes fusion to a type name, filename, or path plus dependency traversal. Use after skeleton review to drill into an area.")]
    public static Task<string> FuseFocusAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [Description("Absolute or relative path to the source directory.")] string path,
        [Description("Type name, filename, or path to scope around.")] string focus,
        [Description("Dependency traversal depth.")] int depth = 1,
        [Description("Directory names to skip.")] string[]? excludeDirectories = null,
        [Description("File names to exclude.")] string[]? excludeFiles = null,
        [Description("Glob patterns to exclude.")] string[]? excludePatterns = null,
        [Description("Exclude all test project directories.")] bool excludeTestProjects = false,
        [Description("C# reduction level: none, standard, aggressive, skeleton, publicApi. Defaults to standard.")] ReductionLevel level = ReductionLevel.Standard,
        [Description("Hard token limit.")] int? maxTokens = null,
        [Description("Include top token-consuming files in the stats comment.")] bool trackTopTokenFiles = false,
        [Description("Session id: omit files already returned under this id earlier in the session.")] string? session = null,
        CancellationToken cancellationToken = default) =>
        FuseToolHelpers.ExecuteDotNetAsync(
            orchestrator,
            templateRegistry,
            path,
            builder =>
            {
                builder
                    .WithEmissionOptions(new EmissionOptions
                    {
                        MaxTokens = maxTokens,
                        ShowTokenCount = false,
                        TrackTopTokenFiles = trackTopTokenFiles,
                        IncludeManifest = true,
                        SessionId = session
                    })
                    .WithReductionOptions(new ReductionOptions(level: level, enableRedaction: true))
                    .WithFocusOptions(new FocusOptions(focus, depth));

                FuseToolHelpers.ApplyCommonFilters(
                    builder,
                    null, null, null,
                    excludeDirectories, excludeFiles, excludePatterns,
                    excludeTestProjects: excludeTestProjects);
            },
            trackTopTokenFiles,
            cancellationToken);

    /// <summary>
    ///     Scopes fusion to files ranked by BM25 relevance to a query.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="path">Absolute or relative path to the source directory.</param>
    /// <param name="query">Natural-language or keyword query ranked with BM25.</param>
    /// <param name="queryTop">Number of top-ranked files used to seed dependency expansion.</param>
    /// <param name="depth">Number of dependency hops to traverse out from the seed files.</param>
    /// <param name="excludeDirectories">Directory names to skip, or <see langword="null" /> for none.</param>
    /// <param name="excludeFiles">File names to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludePatterns">Glob patterns to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludeTestProjects">When <see langword="true" />, skip all test project directories.</param>
    /// <param name="level">The C# reduction level (none, standard, aggressive, skeleton, publicApi).</param>
    /// <param name="maxTokens">Hard token limit at which emission stops, or <see langword="null" /> for unlimited.</param>
    /// <param name="trackTopTokenFiles">When <see langword="true" />, append the top token-consuming files to the trailing stats comment.</param>
    /// <param name="session">Session id for session-delta emission, or <see langword="null" /> to disable it.</param>
    /// <param name="cancellationToken">Token used to cancel the fusion run.</param>
    /// <returns>
    ///     The query-scoped fusion output as a single string, or a descriptive error message when the directory
    ///     is missing or fusion fails.
    /// </returns>
    [McpServerTool(Name = "fuse_search", ReadOnly = true)]
    [Description("Finds where a feature or concept lives in a .NET codebase: BM25-ranked relevant files plus their dependencies, reduced, in one call. Prefer over grep for concept or feature lookups; use grep only for exact strings or symbol names.")]
    public static Task<string> FuseSearchAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [Description("Absolute or relative path to the source directory.")] string path,
        [Description("Natural-language or keyword query.")] string query,
        [Description("Number of top-ranked seed files.")] int queryTop = 10,
        [Description("Dependency traversal depth after seed selection.")] int depth = 1,
        [Description("Directory names to skip.")] string[]? excludeDirectories = null,
        [Description("File names to exclude.")] string[]? excludeFiles = null,
        [Description("Glob patterns to exclude.")] string[]? excludePatterns = null,
        [Description("Exclude all test project directories.")] bool excludeTestProjects = false,
        [Description("C# reduction level: none, standard, aggressive, skeleton, publicApi. Defaults to standard.")] ReductionLevel level = ReductionLevel.Standard,
        [Description("Hard token limit.")] int? maxTokens = null,
        [Description("Include top token-consuming files in the stats comment.")] bool trackTopTokenFiles = false,
        [Description("Session id: omit files already returned under this id earlier in the session.")] string? session = null,
        CancellationToken cancellationToken = default) =>
        FuseToolHelpers.ExecuteDotNetAsync(
            orchestrator,
            templateRegistry,
            path,
            builder =>
            {
                builder
                    .WithEmissionOptions(new EmissionOptions
                    {
                        MaxTokens = maxTokens,
                        ShowTokenCount = false,
                        TrackTopTokenFiles = trackTopTokenFiles,
                        IncludeManifest = true,
                        SessionId = session
                    })
                    .WithReductionOptions(new ReductionOptions(level: level, enableRedaction: true))
                    .WithQueryOptions(new QueryOptions(query, queryTop, depth));

                FuseToolHelpers.ApplyCommonFilters(
                    builder,
                    null, null, null,
                    excludeDirectories, excludeFiles, excludePatterns,
                    excludeTestProjects: excludeTestProjects);
            },
            trackTopTokenFiles,
            cancellationToken);

    /// <summary>
    ///     Scopes fusion to files changed since a git ref.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="path">Absolute or relative path to the source directory.</param>
    /// <param name="changedSince">Git ref (branch, commit, or <c>HEAD~N</c>) to diff the working tree against.</param>
    /// <param name="includeDependents">When <see langword="true" />, also include first-degree dependents of the changed files.</param>
    /// <param name="review">When <see langword="true" />, prepend a review map of diff hunks and direct callers per changed file.</param>
    /// <param name="excludeDirectories">Directory names to skip, or <see langword="null" /> for none.</param>
    /// <param name="excludeFiles">File names to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludePatterns">Glob patterns to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludeTestProjects">When <see langword="true" />, skip all test project directories.</param>
    /// <param name="level">The C# reduction level (none, standard, aggressive, skeleton, publicApi).</param>
    /// <param name="maxTokens">Hard token limit at which emission stops, or <see langword="null" /> for unlimited.</param>
    /// <param name="trackTopTokenFiles">When <see langword="true" />, append the top token-consuming files to the trailing stats comment.</param>
    /// <param name="cancellationToken">Token used to cancel the fusion run.</param>
    /// <returns>
    ///     The change-scoped fusion output as a single string, or a descriptive error message when the directory
    ///     is missing or fusion fails.
    /// </returns>
    [McpServerTool(Name = "fuse_changes", ReadOnly = true)]
    [Description("Change-scoped fusion for a .NET codebase. Returns files changed since a git ref plus optional dependents. Set review=true to prepend diff hunks and direct callers per changed file.")]
    public static Task<string> FuseChangesAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [Description("Absolute or relative path to the source directory.")] string path,
        [Description("Git ref (branch, commit, HEAD~N) to diff against.")] string changedSince,
        [Description("Include first-degree dependents of changed files.")] bool includeDependents = true,
        [Description("Prepend a review map: diff hunks and direct callers for each changed file.")] bool review = false,
        [Description("Directory names to skip.")] string[]? excludeDirectories = null,
        [Description("File names to exclude.")] string[]? excludeFiles = null,
        [Description("Glob patterns to exclude.")] string[]? excludePatterns = null,
        [Description("Exclude all test project directories.")] bool excludeTestProjects = false,
        [Description("C# reduction level: none, standard, aggressive, skeleton, publicApi. Defaults to standard.")] ReductionLevel level = ReductionLevel.Standard,
        [Description("Hard token limit.")] int? maxTokens = null,
        [Description("Include top token-consuming files in the stats comment.")] bool trackTopTokenFiles = false,
        CancellationToken cancellationToken = default) =>
        FuseToolHelpers.ExecuteDotNetAsync(
            orchestrator,
            templateRegistry,
            path,
            builder =>
            {
                builder
                    .WithEmissionOptions(new EmissionOptions
                    {
                        MaxTokens = maxTokens,
                        ShowTokenCount = false,
                        TrackTopTokenFiles = trackTopTokenFiles,
                        IncludeManifest = true
                    })
                    .WithReductionOptions(new ReductionOptions(level: level, enableRedaction: true))
                    .WithChangeOptions(new ChangeOptions(changedSince, includeDependents || review, review));

                FuseToolHelpers.ApplyCommonFilters(
                    builder,
                    null, null, null,
                    excludeDirectories, excludeFiles, excludePatterns,
                    excludeTestProjects: excludeTestProjects);
            },
            trackTopTokenFiles,
            cancellationToken);

    /// <summary>
    ///     Fuses a .NET codebase and returns optimized in-memory context.
    /// </summary>
    /// <remarks>
    ///     The full-control tool that exposes every .NET fusion option. <paramref name="focus" />,
    ///     <paramref name="changedSince" />, and <paramref name="query" /> are mutually exclusive scoping modes:
    ///     each is applied only when non-empty, and supplying more than one fails validation.
    /// </remarks>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="path">Absolute or relative path to the source directory.</param>
    /// <param name="excludeDirectories">Directory names to skip, or <see langword="null" /> for none.</param>
    /// <param name="excludeFiles">File names to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludePatterns">Glob patterns to exclude, or <see langword="null" /> for none.</param>
    /// <param name="includeExtensions">Extensions to add on top of the DotNet template defaults.</param>
    /// <param name="excludeExtensions">Extensions to remove from the DotNet template defaults.</param>
    /// <param name="onlyExtensions">Extensions to use exclusively, ignoring template defaults.</param>
    /// <param name="maxFileSizeKb">Maximum file size in KB; <c>0</c> means unlimited.</param>
    /// <param name="excludeTestProjects">When <see langword="true" />, skip all test project directories.</param>
    /// <param name="excludeUnitTestProjects">When <see langword="true" />, skip only unit test project directories.</param>
    /// <param name="level">The C# reduction level: none, standard, aggressive, skeleton, or publicApi.</param>
    /// <param name="semanticMarkers">When <see langword="true" />, prepend structural annotation comments to each entry.</param>
    /// <param name="focus">Type name, filename, or path to scope around, or <see langword="null" /> to skip focus scoping.</param>
    /// <param name="depth">Dependency traversal depth applied to focus and query scoping.</param>
    /// <param name="changedSince">Git ref to scope to changed files, or <see langword="null" /> to skip change scoping.</param>
    /// <param name="includeDependents">When <see langword="true" />, include first-degree dependents of changed files.</param>
    /// <param name="query">BM25 query to scope fusion, or <see langword="null" /> to skip query scoping.</param>
    /// <param name="queryTop">Number of top-ranked seed files for query scoping.</param>
    /// <param name="patternSummary">When <see langword="true" />, detect and append a cross-codebase pattern summary.</param>
    /// <param name="collapseGenerated">When <see langword="true" />, collapse EF Core migration and model-snapshot bodies to signatures.</param>
    /// <param name="maxTokens">Hard token limit at which emission stops, or <see langword="null" /> for unlimited.</param>
    /// <param name="trackTopTokenFiles">When <see langword="true" />, append the top token-consuming files to the trailing stats comment.</param>
    /// <param name="gitStats">When <see langword="true" />, include git churn stats in the manifest.</param>
    /// <param name="cancellationToken">Token used to cancel the fusion run.</param>
    /// <returns>
    ///     The XML-formatted fused file contents as a single string, or a descriptive error message when the
    ///     directory is missing or fusion fails.
    /// </returns>
    [McpServerTool(Name = "fuse_dotnet", ReadOnly = true)]
    [Description("Full-control .NET fusion with all options (skeleton, focus, change scoping, pattern summary). Returns XML-formatted file contents.")]
    public static async Task<string> FuseDotNetAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [Description("Absolute or relative path to the source directory.")] string path,
        [Description("Directory names to skip.")] string[]? excludeDirectories = null,
        [Description("File names to exclude.")] string[]? excludeFiles = null,
        [Description("Glob patterns to exclude.")] string[]? excludePatterns = null,
        [Description("Extensions to add on top of DotNet template defaults.")] string[]? includeExtensions = null,
        [Description("Extensions to remove from DotNet template defaults.")] string[]? excludeExtensions = null,
        [Description("Extensions to use exclusively, ignoring template defaults.")] string[]? onlyExtensions = null,
        [Description("Maximum file size in KB. Zero means unlimited.")] int maxFileSizeKb = 0,
        [Description("Exclude all test project directories.")] bool excludeTestProjects = false,
        [Description("Exclude only unit test project directories.")] bool excludeUnitTestProjects = false,
        [Description("C# reduction level: none, standard, aggressive, skeleton, publicApi. Defaults to standard.")] ReductionLevel level = ReductionLevel.Standard,
        [Description("Prepend structural annotation comments.")] bool semanticMarkers = false,
        [Description("Type name, filename, or path to scope around.")] string? focus = null,
        [Description("Dependency traversal depth.")] int depth = 1,
        [Description("Git ref to scope to changed files.")] string? changedSince = null,
        [Description("Include first-degree dependents of changed files.")] bool includeDependents = true,
        [Description("BM25 query to scope fusion.")] string? query = null,
        [Description("Number of top-ranked seed files for query scoping.")] int queryTop = 10,
        [Description("Detect and append pattern summary.")] bool patternSummary = false,
        [Description("Collapse EF Core migration and model-snapshot bodies to signatures.")] bool collapseGenerated = false,
        [Description("Hard token limit.")] int? maxTokens = null,
        [Description("Include top token-consuming files in the stats comment.")] bool trackTopTokenFiles = false,
        [Description("Include git churn stats in the manifest.")] bool gitStats = false,
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = Path.GetFullPath(path);
        if (!Directory.Exists(resolvedPath))
            return $"Error: Directory not found: {resolvedPath}";

        try
        {
            var builder = FuseToolHelpers.CreateDotNetBuilder(templateRegistry, resolvedPath)
                .WithMaxFileSizeKb(maxFileSizeKb)
                .WithCollectionBehavior(
                    excludeTestProjects: excludeTestProjects,
                    excludeUnitTestProjects: excludeUnitTestProjects)
                .WithEmissionOptions(new EmissionOptions
                {
                    MaxTokens = maxTokens,
                    ShowTokenCount = false,
                    TrackTopTokenFiles = trackTopTokenFiles,
                    IncludeManifest = true,
                    IncludeGitStats = gitStats
                })
                .WithReductionOptions(new ReductionOptions(
                    level: level,
                    includeSemanticMarkers: semanticMarkers,
                    includePatternSummary: patternSummary,
                    collapseGeneratedCode: collapseGenerated,
                    enableRedaction: true));

            if (!string.IsNullOrWhiteSpace(focus))
                builder.WithFocusOptions(new FocusOptions(focus, depth));

            if (!string.IsNullOrWhiteSpace(changedSince))
                builder.WithChangeOptions(new ChangeOptions(changedSince, includeDependents));

            if (!string.IsNullOrWhiteSpace(query))
                builder.WithQueryOptions(new QueryOptions(query, queryTop, depth));

            FuseToolHelpers.ApplyOptionalFilters(
                builder, onlyExtensions, includeExtensions, excludeExtensions,
                excludeDirectories, excludeFiles, excludePatterns);

            return await FuseToolHelpers.ExecuteInMemoryAsync(
                orchestrator, builder.Build(), trackTopTokenFiles, cancellationToken);
        }
        catch (FusionException ex)
        {
            return $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error during fusion: {ex.Message}";
        }
    }

    /// <summary>
    ///     Fuses a codebase using a named template and returns optimized in-memory context.
    /// </summary>
    /// <remarks>
    ///     Use this for any non-.NET template (Python, Go, Rust, and so on). When <paramref name="template" /> is
    ///     <see langword="null" /> or empty, the orchestrator infers a template from the directory contents.
    /// </remarks>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves template defaults.</param>
    /// <param name="path">Absolute or relative path to the source directory.</param>
    /// <param name="template">Template name (for example <c>Python</c>, <c>Go</c>, <c>Rust</c>), or <see langword="null" /> to infer.</param>
    /// <param name="excludeDirectories">Directory names to skip, or <see langword="null" /> for none.</param>
    /// <param name="excludeFiles">File names to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludePatterns">Glob patterns to exclude, or <see langword="null" /> for none.</param>
    /// <param name="includeExtensions">Extensions to add on top of the template defaults.</param>
    /// <param name="excludeExtensions">Extensions to remove from the template defaults.</param>
    /// <param name="onlyExtensions">Extensions to use exclusively, ignoring template defaults.</param>
    /// <param name="maxFileSizeKb">Maximum file size in KB; <c>0</c> means unlimited.</param>
    /// <param name="excludeTestProjects">When <see langword="true" />, skip all test project directories.</param>
    /// <param name="changedSince">Git ref to scope to changed files, or <see langword="null" /> to skip change scoping.</param>
    /// <param name="includeDependents">When <see langword="true" />, include first-degree dependents of changed files.</param>
    /// <param name="maxTokens">Hard token limit at which emission stops, or <see langword="null" /> for unlimited.</param>
    /// <param name="trackTopTokenFiles">When <see langword="true" />, append the top token-consuming files to the trailing stats comment.</param>
    /// <param name="gitStats">When <see langword="true" />, include git churn stats in the manifest.</param>
    /// <param name="cancellationToken">Token used to cancel the fusion run.</param>
    /// <returns>
    ///     The XML-formatted fused file contents as a single string, or a descriptive error message when the
    ///     directory is missing, the template name is unknown, or fusion fails.
    /// </returns>
    [McpServerTool(Name = "fuse_generic", ReadOnly = true)]
    [Description("Generates optimized codebase context for any template (equivalent to fuse). Returns XML-formatted file contents.")]
    public static async Task<string> FuseGenericAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [Description("Absolute or relative path to the source directory.")] string path,
        [Description("Template name: Python, JavaScript, TypeScript, Go, Rust, Java, etc.")] string? template = null,
        [Description("Directory names to skip.")] string[]? excludeDirectories = null,
        [Description("File names to exclude.")] string[]? excludeFiles = null,
        [Description("Glob patterns to exclude.")] string[]? excludePatterns = null,
        [Description("Extensions to add on top of template defaults.")] string[]? includeExtensions = null,
        [Description("Extensions to remove from template defaults.")] string[]? excludeExtensions = null,
        [Description("Extensions to use exclusively.")] string[]? onlyExtensions = null,
        [Description("Maximum file size in KB. Zero means unlimited.")] int maxFileSizeKb = 0,
        [Description("Exclude all test project directories.")] bool excludeTestProjects = false,
        [Description("Git ref to scope to changed files.")] string? changedSince = null,
        [Description("Include first-degree dependents of changed files.")] bool includeDependents = true,
        [Description("Hard token limit.")] int? maxTokens = null,
        [Description("Include top token-consuming files in the stats comment.")] bool trackTopTokenFiles = false,
        [Description("Include git churn stats in the manifest.")] bool gitStats = false,
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = Path.GetFullPath(path);
        if (!Directory.Exists(resolvedPath))
            return $"Error: Directory not found: {resolvedPath}";

        ProjectTemplate? parsedTemplate = null;
        if (!string.IsNullOrWhiteSpace(template))
        {
            if (!Enum.TryParse<ProjectTemplate>(template, ignoreCase: true, out var t))
            {
                return $"Error: Unknown template '{template}'. Valid values: {string.Join(", ", Enum.GetNames<ProjectTemplate>())}";
            }

            parsedTemplate = t;
        }

        try
        {
            var builder = new FusionRequestBuilder(templateRegistry)
                .WithSourceDirectory(resolvedPath)
                .WithInMemory(true)
                .WithMaxFileSizeKb(maxFileSizeKb)
                .WithCollectionBehavior(excludeTestProjects: excludeTestProjects)
                .WithEmissionOptions(new EmissionOptions
                {
                    MaxTokens = maxTokens,
                    ShowTokenCount = false,
                    TrackTopTokenFiles = trackTopTokenFiles,
                    IncludeManifest = true,
                    IncludeGitStats = gitStats
                })
                .WithReductionOptions(new ReductionOptions(enableRedaction: true));

            if (parsedTemplate.HasValue)
                builder.WithTemplate(parsedTemplate.Value);

            if (!string.IsNullOrWhiteSpace(changedSince))
                builder.WithChangeOptions(new ChangeOptions(changedSince, includeDependents));

            FuseToolHelpers.ApplyOptionalFilters(
                builder, onlyExtensions, includeExtensions, excludeExtensions,
                excludeDirectories, excludeFiles, excludePatterns);

            return await FuseToolHelpers.ExecuteInMemoryAsync(
                orchestrator, builder.Build(), trackTopTokenFiles, cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Error during fusion: {ex.Message}";
        }
    }

    /// <summary>
    ///     Compacts a caller-supplied set of files, or raw content, by running Fuse's reduction without
    ///     collecting a whole directory. Lets an agent shrink context it has already identified.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="path">Base directory for resolving relative <paramref name="files" /> paths.</param>
    /// <param name="files">Explicit file paths to reduce; mutually exclusive with <paramref name="content" />.</param>
    /// <param name="content">Raw content to reduce instead of files; uses <paramref name="extension" /> to pick the reducer.</param>
    /// <param name="extension">The extension that selects the reducer for <paramref name="content" />.</param>
    /// <param name="level">The C# reduction level to apply.</param>
    /// <param name="maxTokens">Token ceiling for the output, or zero for no limit.</param>
    /// <param name="cancellationToken">Token used to cancel the run.</param>
    /// <returns>The reduced output, or a descriptive error message when the input is invalid or fusion fails.</returns>
    [McpServerTool(Name = "fuse_reduce", ReadOnly = true)]
    [Description("Compacts a specific set of files (or raw content) by running Fuse's reduction, without collecting a whole directory. Pass `files` (paths you already identified) or `content` (+ `extension`). Use to shrink context before working with it, when fuse_search or fuse_focus is too broad.")]
    public static Task<string> FuseReduceAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [Description("Base directory for resolving relative file paths. Ignored in content mode.")] string path = ".",
        [Description("File paths to reduce, absolute or relative to path.")] string[]? files = null,
        [Description("Raw content to reduce instead of files. Provide extension to select the reducer.")] string? content = null,
        [Description("Extension that selects the reducer for content (for example .cs, .ts, .py). Defaults to .cs.")] string extension = ".cs",
        [Description("C# reduction level: none, standard, aggressive, skeleton, publicApi. Defaults to standard.")] ReductionLevel level = ReductionLevel.Standard,
        [Description("Maximum tokens the reduced output may use, or 0 for no limit.")] int maxTokens = 0,
        CancellationToken cancellationToken = default)
    {
        int? maxTokenLimit = maxTokens > 0 ? maxTokens : null;

        if (!string.IsNullOrEmpty(content))
            return ReduceRunner.ReduceContentAsync(
                orchestrator, templateRegistry, content, extension, level, maxTokenLimit, cancellationToken);

        if (files is { Length: > 0 })
            return ReduceRunner.ReduceFilesAsync(
                orchestrator, templateRegistry, path, files, level, maxTokenLimit, cancellationToken);

        return Task.FromResult("Error: provide either files (paths) or content to reduce.");
    }
}
