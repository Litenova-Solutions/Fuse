using Fuse.Collection.Models;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Reduction.Options;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     MCP resource definitions for Fuse.
/// </summary>
[McpServerResourceType]
public sealed class FuseResources
{
    /// <summary>
    ///     Reads fused content for a given template and path using default options.
    /// </summary>
    [McpServerResource(
        UriTemplate = "fuse://{template}/{path}",
        Name = "Fused Codebase Context",
        MimeType = "text/plain")]
    [System.ComponentModel.Description("Returns the optimized, minified content of a codebase directory.")]
    public static async Task<string> ReadFuseResourceAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [System.ComponentModel.Description("The project template (dotnet, python, generic, azuredevopswiki).")] string template,
        [System.ComponentModel.Description("Relative path to the directory to fuse.")] string path,
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = Path.GetFullPath(path);

        if (!Directory.Exists(resolvedPath))
        {
            return $"Error: Directory not found: {resolvedPath}";
        }

        ProjectTemplate? parsedTemplate = null;
        if (!string.Equals(template, "generic", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(template))
        {
            if (!Enum.TryParse<ProjectTemplate>(template, ignoreCase: true, out var t))
            {
                return $"Error: Unknown template '{template}'. Valid values: generic, {string.Join(", ", Enum.GetNames<ProjectTemplate>())}";
            }

            parsedTemplate = t;
        }

        try
        {
            var builder = new FusionRequestBuilder(templateRegistry)
                .WithSourceDirectory(resolvedPath)
                .WithInMemory(true)
                .WithEmissionOptions(new EmissionOptions { ShowTokenCount = false })
                .WithReductionOptions(new ReductionOptions());

            if (parsedTemplate.HasValue)
            {
                builder.WithTemplate(parsedTemplate.Value);
            }

            var result = await orchestrator.FuseAsync(builder.Build(), cancellationToken);

            if (string.IsNullOrEmpty(result.InMemoryContent))
            {
                return "No files found matching the criteria.";
            }

            return result.InMemoryContent;
        }
        catch (Exception ex)
        {
            return $"Error during fusion: {ex.Message}";
        }
    }
}
