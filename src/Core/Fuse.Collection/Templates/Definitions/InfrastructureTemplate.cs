using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for infrastructure-as-code projects (Terraform, Kubernetes).
/// </summary>
public sealed class InfrastructureTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.Infrastructure);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
    [
        ".tf",
        ".tfvars",
        ".yaml",
        ".yml",
        ".json",
        ".md",
        ".sh",
        ".ps1",
        ".hcl",
        ".tpl",
        ".env",
        ".properties",
        ".conf",
        ".config"
    ];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
    [
        ".terraform",
        "node_modules",
        ".git",
        ".vs",
        ".idea",
        "bin",
        "obj",
        "dist",
        "build",
        ".pytest_cache",
        "__pycache__",
        "tmp",
        "temp",
        "logs"
    ];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludePatterns =>
    [
        "*.tfstate",
        "*.tfstate.backup",
        "*.tfplan",
        "*.tfvars.json",
        "override.tf",
        "override.tf.json",
        "*_override.tf",
        "*_override.tf.json",
        ".terraformrc",
        "terraform.rc",
        "crash.log",
        "crash.*.log",
        ".terraform.lock.hcl"
    ];
}
