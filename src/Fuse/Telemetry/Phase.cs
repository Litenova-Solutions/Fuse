namespace Fuse.Telemetry;

/// <summary>
///     The name of every timed phase of a request, as it appears in a <see cref="PhaseLine"/>. The evals and the tests
///     read these names back and the result files in <c>evals/results</c> are keyed by them, so a name is changed only
///     together with every reader.
/// </summary>
internal static class Phase
{
    /// <summary>Waiting for the engine's request lock, behind the requests that arrived first.</summary>
    public const string Gate = "gate";

    /// <summary>Folding the file changes seen since the last request into both solutions.</summary>
    public const string Sync = "sync";

    /// <summary>Loading the projects a request needs, the owners of the targets and then their dependents.</summary>
    public const string Load = "load";

    /// <summary>Binding the targets and keeping the errors HEAD does not have.</summary>
    public const string BindTargets = "bindTargets";

    /// <summary>Comparing each target's declarations with HEAD, from syntax, to find the declaration changes.</summary>
    public const string SurfaceDiff = "surfaceDiff";

    /// <summary>Finding the files in the dependents that use a changed declaration.</summary>
    public const string ReferenceSearch = "referenceSearch";

    /// <summary>Binding the candidates, or whole projects past the candidate threshold.</summary>
    public const string BindCandidates = "bindCandidates";

    /// <summary>Selecting the affected tests in each test project.</summary>
    public const string Selection = "selection";

    /// <summary>Copying build output into the shadow folders and emitting the changed assemblies into them.</summary>
    public const string Mirror = "mirror";

    /// <summary>The whole request once it holds the request lock, as the engine measured it.</summary>
    public const string Total = "total";
}
