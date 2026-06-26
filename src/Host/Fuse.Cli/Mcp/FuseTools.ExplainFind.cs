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
    ///     Previews which files a scoped .NET fusion would include and exclude, with a per-file token estimate,
    ///     without returning any file bodies.
    /// </summary>
    /// <param name="explainService">The unified explain service.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="path">Absolute or relative path to the source directory.</param>
    /// <param name="focus">A type, file, or path to scope around, or <see langword="null" />.</param>
    /// <param name="query">A relevance query to scope by, or <see langword="null" />.</param>
    /// <param name="changedSince">A git ref to scope to changed files, or <see langword="null" />.</param>
    /// <param name="depth">Dependency traversal depth for focus or query scoping.</param>
    /// <param name="queryTop">Number of top-ranked files to seed query scoping.</param>
    /// <param name="level">C# reduction level used for the token estimate.</param>
    /// <param name="maxTokens">Hard token limit applied to the previewed selection, or <see langword="null" />.</param>
    /// <param name="excludeDirectories">Directory names to skip, or <see langword="null" />.</param>
    /// <param name="excludeFiles">File names to exclude, or <see langword="null" />.</param>
    /// <param name="excludePatterns">Glob patterns to exclude, or <see langword="null" />.</param>
    /// <param name="excludeTestProjects">When <see langword="true" />, skip all test project directories.</param>
    /// <param name="cancellationToken">Token used to cancel collection and the in-memory fusion.</param>
    /// <returns>The explanation text (scope, included files with token costs, excluded files, estimated total), or an error message.</returns>
    [McpServerTool(Name = "fuse_explain", ReadOnly = true)]
    [Description("Preview which files a scoped .NET fusion would include and exclude, with a per-file token estimate, without returning file bodies. Use to check the effect of a focus seed, query, or change range before fetching the context.")]
    public static async Task<string> FuseExplainAsync(
        IExplainService explainService,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [Description("Absolute or relative path to the source directory.")] string path,
        [Description("A type, file, or path to scope around.")] string? focus = null,
        [Description("A relevance query to scope by.")] string? query = null,
        [Description("A git ref (branch, commit, HEAD~N) to scope to changed files.")] string? changedSince = null,
        [Description("Dependency traversal depth for focus or query scoping.")] int depth = 1,
        [Description("Number of top-ranked files to seed query scoping.")] int queryTop = 10,
        [Description("C# reduction level used for the token estimate: none, standard, aggressive, skeleton, publicApi.")] ReductionLevel level = ReductionLevel.Standard,
        [Description("Hard token limit applied to the previewed selection.")] int? maxTokens = null,
        [Description("Directory names to skip.")] string[]? excludeDirectories = null,
        [Description("File names to exclude.")] string[]? excludeFiles = null,
        [Description("Glob patterns to exclude.")] string[]? excludePatterns = null,
        [Description("Exclude all test project directories.")] bool excludeTestProjects = false,
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = Path.GetFullPath(path);
        if (!Directory.Exists(resolvedPath))
            return $"Error: Directory not found: {resolvedPath}";

        try
        {
            var builder = FuseToolHelpers.CreateDotNetBuilder(templateRegistry, resolvedPath)
                .WithReductionOptions(new ReductionOptions(level: level, enableRedaction: true))
                .WithEmissionOptions(new EmissionOptions { MaxTokens = maxTokens, ShowTokenCount = false, IncludeManifest = false });

            FusionRequestComposer.ApplyExclusiveScope(builder, focus, query, changedSince, depth, queryTop);

            FuseToolHelpers.ApplyCommonFilters(
                builder, null, null, null, excludeDirectories, excludeFiles, excludePatterns,
                excludeTestProjects: excludeTestProjects);

            var request = builder.Build();
            var preview = await explainService.PreviewAsync(request, cancellationToken);

            var lines = Verification.ExplanationBuilder.Build(
                preview.ScopeDescription,
                request.Emission.TokenizerModel,
                preview.FusionResult.EmittedFileTokens ?? [],
                preview.CollectedPaths);

            return string.Join("\n", lines);
        }
        catch (FusionValidationException ex)
        {
            return "Error: " + string.Join("; ", ex.Errors);
        }
        catch (Exception ex)
        {
            return $"Error during explain: {ex.Message}";
        }
    }

    /// <summary>
    ///     Performs a cheap, Fuse-native exact lookup over a .NET codebase: a symbol definition, an exact text
    ///     substring with context, or a path match. One coherent interface in place of broad grep.
    /// </summary>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="collectionPipeline">Collection pipeline used to enumerate the files to search.</param>
    /// <param name="contentProviderFactory">Factory for a per-run content provider used to read file bodies.</param>
    /// <param name="secretRedactor">Redactor applied to text-mode snippet output before it is returned.</param>
    /// <param name="outlineExtractors">Outline extractors, resolved by extension, used to enumerate declarations in symbol mode.</param>
    /// <param name="path">Absolute or relative path to the source directory.</param>
    /// <param name="query">The symbol name, exact text, or path fragment to find.</param>
    /// <param name="mode">Which lookup to run: <see cref="FindMode.Symbol" />, <see cref="FindMode.Text" />, or <see cref="FindMode.Path" />.</param>
    /// <param name="ignoreCase">When <see langword="true" />, match case-insensitively (symbol matching is exact-name but case-folded).</param>
    /// <param name="maxMatches">The largest number of matches to return; further matches are summarized as a count.</param>
    /// <param name="contextLines">In text mode, the number of lines of context to show on each side of a match.</param>
    /// <param name="excludeDirectories">Directory names to skip, or <see langword="null" />.</param>
    /// <param name="excludeFiles">File names to exclude, or <see langword="null" />.</param>
    /// <param name="excludePatterns">Glob patterns to exclude, or <see langword="null" />.</param>
    /// <param name="excludeTestProjects">When <see langword="true" />, skip all test project directories.</param>
    /// <param name="cancellationToken">Token used to cancel collection and the search.</param>
    /// <returns>The matches as plain text (one block per file), a no-match note, or an error message.</returns>
    [McpServerTool(Name = "fuse_find", ReadOnly = true)]
    [Description("Cheap Fuse-native exact lookup: find a symbol definition (mode=symbol), an exact text substring with context (mode=text), or files by path fragment (mode=path). Prefer this over broad grep for a single coherent interface; it returns locations, not fused context.")]
    public static async Task<string> FuseFindAsync(
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        Fuse.Collection.FileCollectionPipeline collectionPipeline,
        Func<Fuse.Collection.FileSystem.ISourceContentProvider> contentProviderFactory,
        ISecretRedactor secretRedactor,
        Fuse.Plugins.Abstractions.CapabilityRegistry<Fuse.Plugins.Abstractions.Outline.ISymbolOutlineExtractor> outlineExtractors,
        [Description("Absolute or relative path to the source directory.")] string path,
        [Description("The symbol name, exact text, or path fragment to find.")] string query,
        [Description("Lookup mode: symbol (a declared type or member), text (exact substring with context), or path (filename or path fragment).")] FindMode mode = FindMode.Symbol,
        [Description("Match case-insensitively.")] bool ignoreCase = false,
        [Description("Largest number of matches to return. Defaults to 50.")] int maxMatches = 50,
        [Description("Lines of context shown on each side of a text-mode match. Defaults to 2.")] int contextLines = 2,
        [Description("Directory names to skip.")] string[]? excludeDirectories = null,
        [Description("File names to exclude.")] string[]? excludeFiles = null,
        [Description("Glob patterns to exclude.")] string[]? excludePatterns = null,
        [Description("Exclude all test project directories.")] bool excludeTestProjects = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Error: query must not be empty.";
        if (maxMatches <= 0)
            return "Error: maxMatches must be positive.";

        var resolvedPath = Path.GetFullPath(path);
        if (!Directory.Exists(resolvedPath))
            return $"Error: Directory not found: {resolvedPath}";

        try
        {
            var builder = FuseToolHelpers.CreateDotNetBuilder(templateRegistry, resolvedPath);
            FuseToolHelpers.ApplyCommonFilters(
                builder, null, null, null, excludeDirectories, excludeFiles, excludePatterns,
                excludeTestProjects: excludeTestProjects);

            var request = builder.Build();
            var collection = await collectionPipeline.CollectAsync(request.Collection, request.Parallelism, cancellationToken);

            return mode switch
            {
                FindMode.Path => FindByPath(collection.Files, query, ignoreCase, maxMatches),
                FindMode.Symbol => await FindBySymbolAsync(
                    collection.Files, query, ignoreCase, maxMatches, outlineExtractors, contentProviderFactory(), cancellationToken),
                FindMode.Text => await FindByTextAsync(
                    collection.Files, query, ignoreCase, maxMatches, Math.Max(0, contextLines), contentProviderFactory(), secretRedactor, cancellationToken),
                _ => $"Error: unknown mode '{mode}'."
            };
        }
        catch (Exception ex)
        {
            return $"Error during find: {ex.Message}";
        }
    }

    // Path mode: substring match against the normalized relative path.
    private static string FindByPath(IReadOnlyList<SourceFile> files, string query, bool ignoreCase, int maxMatches)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var matches = files
            .Where(f => f.NormalizedRelativePath.Contains(query, comparison))
            .Select(f => f.NormalizedRelativePath)
            .ToList();

        if (matches.Count == 0)
            return $"No files found whose path contains '{query}'.";

        var shown = matches.Take(maxMatches).ToList();
        var header = $"fuse_find path '{query}': {matches.Count} file(s)";
        var body = string.Join("\n", shown);
        return matches.Count > shown.Count
            ? $"{header}\n{body}\n... and {matches.Count - shown.Count} more"
            : $"{header}\n{body}";
    }

    // Symbol mode: list every declaration (type or member) whose simple name matches the query exactly.
    private static async Task<string> FindBySymbolAsync(
        IReadOnlyList<SourceFile> files,
        string query,
        bool ignoreCase,
        int maxMatches,
        Fuse.Plugins.Abstractions.CapabilityRegistry<Fuse.Plugins.Abstractions.Outline.ISymbolOutlineExtractor> outlineExtractors,
        Fuse.Collection.FileSystem.ISourceContentProvider contentProvider,
        CancellationToken cancellationToken)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var hits = new List<string>();
        var total = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extractor = outlineExtractors.TryResolve(file.Extension);
            if (extractor is null)
                continue;

            var content = await contentProvider.GetContentAsync(file, cancellationToken);
            foreach (var symbol in extractor.ExtractOutline(content))
            {
                if (string.Equals(symbol.Name, query, comparison))
                {
                    total++;
                    if (hits.Count < maxMatches)
                        hits.Add($"{file.NormalizedRelativePath}: {symbol.Kind} {symbol.Name}");
                }

                foreach (var member in symbol.Members)
                {
                    if (string.Equals(member, query, comparison))
                    {
                        total++;
                        if (hits.Count < maxMatches)
                            hits.Add($"{file.NormalizedRelativePath}: member {member} in {symbol.Name}");
                    }
                }
            }
        }

        if (total == 0)
            return $"No symbol named '{query}' found.";

        var header = $"fuse_find symbol '{query}': {total} declaration(s)";
        var body = string.Join("\n", hits);
        return total > hits.Count
            ? $"{header}\n{body}\n... and {total - hits.Count} more"
            : $"{header}\n{body}";
    }

    // Text mode: exact substring match per line, with context lines around each hit.
    private static async Task<string> FindByTextAsync(
        IReadOnlyList<SourceFile> files,
        string query,
        bool ignoreCase,
        int maxMatches,
        int contextLines,
        Fuse.Collection.FileSystem.ISourceContentProvider contentProvider,
        ISecretRedactor secretRedactor,
        CancellationToken cancellationToken)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var blocks = new List<string>();
        var total = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var content = await contentProvider.GetContentAsync(file, cancellationToken);
            if (!content.Contains(query, comparison))
                continue;

            var lines = content.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(query, comparison))
                    continue;

                total++;
                if (blocks.Count >= maxMatches)
                    continue;

                var start = Math.Max(0, i - contextLines);
                var end = Math.Min(lines.Length - 1, i + contextLines);
                var snippet = new System.Text.StringBuilder();
                snippet.Append($"{file.NormalizedRelativePath}:{i + 1}");
                for (var l = start; l <= end; l++)
                    snippet.Append($"\n{l + 1,6}{(l == i ? " > " : "   ")}{lines[l]}");
                blocks.Add(secretRedactor.Redact(snippet.ToString()).Content);
            }
        }

        if (total == 0)
            return $"No text matching '{query}' found.";

        var header = $"fuse_find text '{query}': {total} match(es)";
        var body = string.Join("\n\n", blocks);
        return total > blocks.Count
            ? $"{header}\n\n{body}\n\n... and {total - blocks.Count} more"
            : $"{header}\n\n{body}";
    }
}
