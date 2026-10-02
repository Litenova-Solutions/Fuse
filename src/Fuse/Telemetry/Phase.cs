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

    /// <summary>Loading the projects a request needs: for a check, the owners of the targets.</summary>
    public const string Load = "load";

    /// <summary>Loading the dependents of the projects with declaration changes, which a check binds candidates in.</summary>
    public const string LoadDependents = "loadDependents";

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

    /// <summary>
    ///     Preparing the shadow folders, after the request lock is released: judging which assemblies are stale and copying
    ///     the test project's build output into the shadow folder, everything but <see cref="Emit"/>.
    /// </summary>
    public const string Mirror = "mirror";

    /// <summary>
    ///     Emitting the changed assemblies from the compilations of the snapshot the plan took, and writing them into the
    ///     shadow folders, after the request lock is released.
    /// </summary>
    public const string Emit = "emit";

    /// <summary>
    ///     The whole request from the moment it holds the request lock until it is answered, as the engine measured it. A
    ///     test plan's <see cref="Mirror"/> and <see cref="Emit"/> are inside it, although the lock is released before them.
    /// </summary>
    public const string Total = "total";
}
