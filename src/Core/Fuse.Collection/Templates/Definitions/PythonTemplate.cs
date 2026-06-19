using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for Python projects.
/// </summary>
public sealed class PythonTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.Python);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".py", ".pyc", ".pyd", ".pyo", ".pyw", ".pyx", ".pxd", ".pxi", ".ipynb", ".req", ".txt"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        ["__pycache__", ".venv", "venv", "env", ".tox", "dist", "build", ".git", ".pytest_cache"];
}
