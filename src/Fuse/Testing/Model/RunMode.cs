namespace Fuse.Testing.Model;

/// <summary>How one test assembly runs: as a shadow run, or through a build with MSBuild.</summary>
internal abstract record RunMode
{
    private RunMode()
    {
    }

    /// <summary>
    ///     A shadow run: the test assembly runs from a copy of its build output with the changed assemblies emitted
    ///     into it, so <c>dotnet test</c> starts no build.
    /// </summary>
    /// <param name="Assembly">Absolute path of the test assembly in the shadow directory.</param>
    public sealed record Shadow(string Assembly) : RunMode;

    /// <summary>
    ///     <c>dotnet test</c> builds the project with MSBuild first. It is the mode when a shadow run is not safe (a
    ///     project has no build output, a file other than C# changed since that build, or a changed project does not
    ///     compile), for a project on Microsoft.Testing.Platform, and for every project when all tests run.
    /// </summary>
    public sealed record Build : RunMode;
}
