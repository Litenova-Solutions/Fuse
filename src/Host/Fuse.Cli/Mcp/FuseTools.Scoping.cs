using System.ComponentModel;
using Fuse.Fusion.Scoping;
using Fuse.Collection.Models;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Reduction.Security;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

public sealed partial class FuseTools
{
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
    [Description("Scopes fusion to a type name, filename, or path plus dependency traversal. Use when you already know the type, file, or area to explore or edit (after a skeleton survey, or when a task names a type); use fuse_search instead when you do not yet know where a concept lives, and fuse_changes for branch or PR work.")]
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
    [Description("Finds where a feature or concept lives in a .NET codebase when you do not yet know which file holds it: BM25-ranked relevant files plus their dependencies, reduced, in one call. Prefer over grep for concept or feature lookups; use grep only for exact strings or symbol names, fuse_focus when you already know the type, and fuse_changes for branch or PR work.")]
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
    [Description("Change-scoped fusion for a .NET codebase. Prefer this for branch, PR, or fix work whenever a git base is available: starting from the diff, it has by far the highest recall of the files a task touches. Returns files changed since a git ref plus optional dependents. Set review=true to prepend diff hunks and direct callers per changed file.")]
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
}
