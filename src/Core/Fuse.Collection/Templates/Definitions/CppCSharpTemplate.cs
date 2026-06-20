using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for mixed C++ and C# projects.
/// </summary>
public sealed class CppCSharpTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.CppCSharp);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".cpp", ".hpp", ".h", ".c", ".cc", ".cs", ".csproj", ".sln"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        ["bin", "obj", "Debug", "Release", "x64", "x86", ".vs", ".git"];
}
