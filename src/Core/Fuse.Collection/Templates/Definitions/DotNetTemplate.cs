using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template optimized for .NET projects (C#, F#, VB.NET, ASP.NET).
/// </summary>
public sealed class DotNetTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.DotNet);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
    [
        ".cs", ".razor", ".cshtml", ".xaml",
        ".csproj", ".props", ".targets", ".config",
        ".json", ".xml",
        ".yml", ".yaml",
        ".md",
        ".scss", ".css", ".html", ".htm",
        ".editorconfig"
    ];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
    [
        "bin", "obj", ".vs", ".git", ".idea",
        "node_modules", ".next",
        "TestResults",
        "packages",
        "artifacts"
    ];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludePatterns =>
    [
        "*.feature.cs",
        "*Steps.g.cs",
        "*.AssemblyHooks.cs",
        "*.g.cs",
        "*.g.i.cs",
        "*.Designer.cs",
        "*.designer.cs",
        "*_i.c",
        "*.generated.cs",
        "TemporaryGeneratedFile_*.cs",
        "*.Cache.cs",
        "*.cache",
        "*.baml",
        "ServiceReference.cs",
        "Reference.cs",
        "AssemblyInfo.cs",
        "*.xsd.cs",
        "*.resx",
        "*.resources",
        "launchSettings.json",
        "packages.lock.json",
        "bundleconfig.json",
        "*.min.js",
        "*.min.css",
        "*.map",
        "package-lock.json",
        "yarn.lock"
    ];
}
