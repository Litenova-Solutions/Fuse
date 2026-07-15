using Fuse.Indexing;

namespace Fuse.Semantics;

/// <summary>
///     Canonicalizes the compiler captures of multi-target projects without discarding declarations that exist only
///     in a non-primary target framework.
/// </summary>
/// <remarks>
///     The primary target framework is deterministic: a modern <c>netX.Y</c> target wins, then
///     <c>netcoreapp</c>, then <c>netstandard</c>, then legacy framework targets; within a family the highest parsed
///     version wins and a final ordinal target-framework comparison breaks ties. Every entity from every capture is
///     still unioned by its stable id. The primary decides only which otherwise-identical record supplies the stored
///     representation; <see cref="Availability" /> preserves all target frameworks in which that id exists.
/// </remarks>
internal sealed class CanonicalTfmUnion
{
    private CanonicalTfmUnion(
        IReadOnlyList<CapturedProject> projects,
        IReadOnlyList<SymbolRecord> symbols,
        IReadOnlyList<NodeRecord> nodes,
        IReadOnlyList<SemanticEdgeRecord> edges,
        IReadOnlyList<RouteRecord> routes,
        IReadOnlyList<DiRegistrationRecord> registrations,
        IReadOnlyList<OptionsBindingRecord> bindings,
        IReadOnlyList<TfmAvailabilityRecord> availability,
        int capturedProjectCount,
        int rawEntityCount)
    {
        Projects = projects;
        Symbols = symbols;
        Nodes = nodes;
        Edges = edges;
        Routes = routes;
        Registrations = registrations;
        Bindings = bindings;
        Availability = availability;
        CapturedProjectCount = capturedProjectCount;
        RawEntityCount = rawEntityCount;
    }

    /// <summary>The one primary record for each project file.</summary>
    public IReadOnlyList<CapturedProject> Projects { get; }

    /// <summary>The canonical union of symbols.</summary>
    public IReadOnlyList<SymbolRecord> Symbols { get; }

    /// <summary>The canonical union of nodes.</summary>
    public IReadOnlyList<NodeRecord> Nodes { get; }

    /// <summary>The canonical union of edges.</summary>
    public IReadOnlyList<SemanticEdgeRecord> Edges { get; }

    /// <summary>The canonical union of routes.</summary>
    public IReadOnlyList<RouteRecord> Routes { get; }

    /// <summary>The canonical union of DI registrations.</summary>
    public IReadOnlyList<DiRegistrationRecord> Registrations { get; }

    /// <summary>The canonical union of options bindings.</summary>
    public IReadOnlyList<OptionsBindingRecord> Bindings { get; }

    /// <summary>The target-framework availability facts for the canonical union.</summary>
    public IReadOnlyList<TfmAvailabilityRecord> Availability { get; }

    /// <summary>The compiler invocation count represented by the capture.</summary>
    public int CapturedProjectCount { get; }

    /// <summary>The entity count before canonical de-duplication.</summary>
    public int RawEntityCount { get; }

    /// <summary>The entity count after canonical de-duplication.</summary>
    public int CanonicalEntityCount => Symbols.Count + Nodes.Count + Edges.Count + Routes.Count + Registrations.Count + Bindings.Count;

    /// <summary>Builds the deterministic canonical union for one complete build capture.</summary>
    /// <param name="capture">The successful build capture to canonicalize.</param>
    /// <returns>The canonical union and availability facts.</returns>
    public static CanonicalTfmUnion Create(CaptureResult capture)
    {
        ArgumentNullException.ThrowIfNull(capture);

        var ordered = capture.Projects
            .Select((project, ordinal) => new OrderedProject(project, ordinal))
            .OrderBy(item => ProjectKey(item.Project), StringComparer.Ordinal)
            .ThenByDescending(item => TargetFrameworkRank(item.Project.TargetFramework).Family)
            .ThenByDescending(item => TargetFrameworkRank(item.Project.TargetFramework).Version)
            .ThenBy(item => item.Project.TargetFramework ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(item => item.Ordinal)
            .ToList();

        var primaryProjects = ordered
            .GroupBy(item => ProjectKey(item.Project), StringComparer.Ordinal)
            .Select(group => group.First().Project)
            .ToList();
        var availability = new List<TfmAvailabilityRecord>();
        foreach (var project in ordered)
            AddAvailability(availability, "project", ProjectKey(project.Project), project.Project.TargetFramework);

        var symbols = Union(ordered, static project => project.Symbols, static symbol => symbol.SymbolId, "symbol", availability);
        var nodes = Union(ordered, static project => project.Nodes, static node => node.NodeId, "node", availability);
        var edges = Union(ordered, static project => project.Edges, BuildEdgeAvailabilityId, "edge", availability);
        var routes = Union(ordered, static project => project.Routes, static route => route.RouteId, "route", availability);
        var registrations = Union(ordered, static project => project.DiRegistrations, static registration => registration.RegistrationId, "di_registration", availability);
        var bindings = Union(ordered, static project => project.OptionsBindings, static binding => binding.BindingId, "options_binding", availability);

        var rawEntityCount = capture.Projects.Sum(project =>
            (project.Symbols?.Count ?? 0) +
            (project.Nodes?.Count ?? 0) +
            (project.Edges?.Count ?? 0) +
            (project.Routes?.Count ?? 0) +
            (project.DiRegistrations?.Count ?? 0) +
            (project.OptionsBindings?.Count ?? 0));
        var canonicalAvailability = availability
            .Distinct()
            .OrderBy(item => item.EntityKind, StringComparer.Ordinal)
            .ThenBy(item => item.EntityId, StringComparer.Ordinal)
            .ThenBy(item => item.TargetFramework, StringComparer.Ordinal)
            .ToList();

        return new CanonicalTfmUnion(
            primaryProjects,
            symbols,
            nodes,
            edges,
            routes,
            registrations,
            bindings,
            canonicalAvailability,
            capture.Projects.Count,
            rawEntityCount);
    }

    private static List<T> Union<T>(
        IReadOnlyList<OrderedProject> projects,
        Func<CapturedProject, IReadOnlyList<T>?> records,
        Func<T, string> id,
        string entityKind,
        List<TfmAvailabilityRecord> availability)
    {
        var union = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            foreach (var record in records(project.Project) ?? [])
            {
                union.TryAdd(id(record), record);
                AddAvailability(availability, entityKind, id(record), project.Project.TargetFramework);
            }
        }

        return union
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value)
            .ToList();
    }

    private static void AddAvailability(
        List<TfmAvailabilityRecord> availability,
        string entityKind,
        string entityId,
        string? targetFramework)
    {
        if (!string.IsNullOrWhiteSpace(targetFramework))
            availability.Add(new TfmAvailabilityRecord(entityKind, entityId, targetFramework));
    }

    // Edge ids are assigned by the SQLite writer after it resolves the evidence file's integer id. The canonical
    // union runs before that write, so availability uses the equivalent stable source identity instead.
    private static string BuildEdgeAvailabilityId(SemanticEdgeRecord edge) =>
        string.Concat(
            edge.FromNodeId, "\u0001",
            edge.ToNodeId, "\u0001",
            edge.EdgeType, "\u0001",
            edge.EvidenceFilePath ?? string.Empty);

    private static string ProjectKey(CapturedProject project) =>
        string.IsNullOrWhiteSpace(project.FilePath) ? project.Name : project.FilePath;

    private static TargetFrameworkOrdering TargetFrameworkRank(string? targetFramework)
    {
        if (string.IsNullOrWhiteSpace(targetFramework))
            return new TargetFrameworkOrdering(-1, new Version(0, 0));

        var tfm = targetFramework.Trim();
        if (tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            && tfm.Length > 3
            && char.IsDigit(tfm[3])
            && tfm.Contains('.', StringComparison.Ordinal))
            return new TargetFrameworkOrdering(3, ParseVersion(tfm[3..]));
        if (tfm.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
            return new TargetFrameworkOrdering(2, ParseVersion(tfm[10..]));
        if (tfm.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
            return new TargetFrameworkOrdering(1, ParseVersion(tfm[11..]));
        return new TargetFrameworkOrdering(0, new Version(0, 0));
    }

    private static Version ParseVersion(string text) =>
        Version.TryParse(text.TrimStart('v'), out var parsed) ? parsed : new Version(0, 0);

    private sealed record OrderedProject(CapturedProject Project, int Ordinal);

    private sealed record TargetFrameworkOrdering(int Family, Version Version);
}
