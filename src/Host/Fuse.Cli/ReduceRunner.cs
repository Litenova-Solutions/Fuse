using Fuse.Cli.Mcp;
using Fuse.Collection.Templates;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Plugins.Abstractions.Options;

namespace Fuse.Cli;

/// <summary>
///     Runs the reduction pipeline over a caller-supplied set of files or raw content, without collecting a
///     whole directory. Shared by the <c>fuse reduce</c> CLI command and the <c>fuse_reduce</c> MCP tool.
/// </summary>
internal static class ReduceRunner
{
    /// <summary>
    ///     Reduces exactly the given files and returns the fused, reduced output.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="baseDirectory">Base directory for resolving relative file paths.</param>
    /// <param name="files">File paths, absolute or relative to <paramref name="baseDirectory" />.</param>
    /// <param name="level">The C# reduction level to apply.</param>
    /// <param name="maxTokens">Optional token ceiling for the emitted output.</param>
    /// <param name="cancellationToken">Token used to cancel the run.</param>
    /// <returns>The reduced output, or a descriptive error string when the input is invalid or fusion fails.</returns>
    public static async Task<string> ReduceFilesAsync(
        FusionOrchestrator orchestrator,
        ProjectTemplateRegistry templateRegistry,
        string baseDirectory,
        IReadOnlyList<string> files,
        ReductionLevel level,
        int? maxTokens,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
            return "Error: no files to reduce. Provide at least one file path.";

        var resolvedBase = Path.GetFullPath(baseDirectory);
        if (!Directory.Exists(resolvedBase))
            return $"Error: Directory not found: {resolvedBase}";

        try
        {
            var builder = FuseToolHelpers.CreateDotNetBuilder(templateRegistry, resolvedBase)
                .WithExplicitFiles(files)
                .WithReductionOptions(new ReductionOptions(
                    level: level,
                    trimContent: true,
                    useCondensing: true,
                    enableRedaction: true))
                .WithEmissionOptions(new EmissionOptions
                {
                    ShowTokenCount = false,
                    IncludeManifest = true,
                    MaxTokens = maxTokens
                });

            return await FuseToolHelpers.ExecuteInMemoryAsync(
                orchestrator, builder.Build(), trackTopTokenFiles: false, cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Error during reduction: {ex.Message}";
        }
    }

    /// <summary>
    ///     Reduces raw content by materializing it as a single temporary file (so the reducer is selected by
    ///     extension), then deletes the temporary file.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="content">The raw content to reduce.</param>
    /// <param name="extension">The file extension that selects the reducer (for example <c>.cs</c>).</param>
    /// <param name="level">The C# reduction level to apply.</param>
    /// <param name="maxTokens">Optional token ceiling for the emitted output.</param>
    /// <param name="cancellationToken">Token used to cancel the run.</param>
    /// <returns>The reduced output, or a descriptive error string when fusion fails.</returns>
    public static async Task<string> ReduceContentAsync(
        FusionOrchestrator orchestrator,
        ProjectTemplateRegistry templateRegistry,
        string content,
        string extension,
        ReductionLevel level,
        int? maxTokens,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(content))
            return "Error: no content to reduce.";

        var normalizedExtension = extension.StartsWith('.') ? extension : "." + extension;
        var tempDirectory = Path.Combine(Path.GetTempPath(), "fuse-reduce-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var fileName = "input" + normalizedExtension;

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDirectory, fileName), content, cancellationToken);
            return await ReduceFilesAsync(
                orchestrator, templateRegistry, tempDirectory, [fileName], level, maxTokens, cancellationToken);
        }
        finally
        {
            // Best-effort cleanup; a leaked temp file must never fail the reduction.
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures.
            }
        }
    }
}
