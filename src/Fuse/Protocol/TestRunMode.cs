using System.Text.Json.Serialization;

namespace Fuse.Protocol;

/// <summary>
///     How the client runs one test assembly. The JSON names the case in a <c>kind</c> property, written first, which
///     System.Text.Json reads to choose the record to create.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Shadow), nameof(Shadow))]
[JsonDerivedType(typeof(Build), nameof(Build))]
internal abstract record TestRunMode
{
    private TestRunMode()
    {
    }

    /// <summary>
    ///     Run the test assembly from its shadow directory, which already holds the changed assemblies, so
    ///     <c>dotnet test</c> hands it to VSTest without a build. Shadow runs touch no build output, so they run in
    ///     parallel.
    /// </summary>
    /// <param name="Assembly">Absolute path of the test assembly in the shadow directory.</param>
    public sealed record Shadow(string Assembly) : TestRunMode;

    /// <summary>
    ///     Run <c>dotnet test</c> on the project, which builds it with MSBuild first. These runs share <c>obj</c> and
    ///     <c>bin</c> folders across projects, so they run one after another.
    /// </summary>
    public sealed record Build : TestRunMode;
}
