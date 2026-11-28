using Fuse.Identity;
using Fuse.Paths;
namespace Fuse.Operations.Test;

/// <summary>
///     What <c>fuse test</c> runs: the repository and directory it runs in, the <c>dotnet test</c> arguments
///     that choose the scope, which scope they choose, and the knobs every run honors.
/// </summary>
/// <param name="Root">The repository the run is for.</param>
/// <param name="WorkingDirectory">Where <c>dotnet test</c> runs.</param>
/// <param name="Arguments">The caller's <c>dotnet test</c> arguments.</param>
/// <param name="All">True to run every test instead of the affected ones.</param>
/// <param name="Session">The session the run is for, as its harness identifies it, or null.</param>
/// <param name="Live">What the first stage's failures print to as soon as they are known, or null to print nothing until the answer.</param>
/// <param name="Failed">True to rerun the tests that failed in the last recorded run instead of planning from the changes.</param>
/// <param name="Clean">True to clear the slice test cache and run fresh processes.</param>
/// <param name="NoCache">True to bypass the slice test cache and reused test results.</param>
/// <param name="MaxFailures">Maximum test failures before stopping early.</param>
internal sealed record TestInvocation(
    RepoRoot Root,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    bool All,
    string? Session,
    TextWriter? Live,
    bool Failed,
    bool Clean,
    bool NoCache,
    ErrorLimit MaxFailures);
