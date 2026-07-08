namespace Fuse.Semantics.Remediation;

/// <summary>
///     One project's line in a remediation plan (C1): whether it loaded, and, when it did not, the knowledge-base
///     signature that classifies its failure (or null when the failure is unrecognized).
/// </summary>
/// <param name="Project">The project name.</param>
/// <param name="Loaded">Whether the project loaded to a compilation (oracle-grade for that project).</param>
/// <param name="Reason">The concrete load reason reported by the loader.</param>
/// <param name="Signature">The matched remediation signature, or null when loaded or when the failure is unrecognized.</param>
public sealed record RemediationPlanItem(string Project, bool Loaded, string Reason, RemediationSignature? Signature);

/// <summary>
///     A remediation plan for a workspace (C1): what <c>fuse up</c> would do, computed from a load diagnosis and
///     the knowledge base, before any remedy is applied. It names, per project, whether the project is workable
///     and which remedy (if any) addresses its failure, plus the workable-subset summary an agent reads at minute
///     zero.
/// </summary>
/// <param name="Tier">The achieved load tier (semantic, partial, or syntax).</param>
/// <param name="ProjectsLoaded">The number of projects that loaded to a compilation.</param>
/// <param name="ProjectsTotal">The total number of projects opened.</param>
/// <param name="Items">The per-project plan lines.</param>
public sealed record RemediationPlan(
    string Tier,
    int ProjectsLoaded,
    int ProjectsTotal,
    IReadOnlyList<RemediationPlanItem> Items)
{
    /// <summary>The downgraded projects that have a matched, applicable (not classify-only) remedy.</summary>
    public IReadOnlyList<RemediationPlanItem> Remediable =>
        Items.Where(i => !i.Loaded && i.Signature is { Remedy: not "classify-only" }).ToList();

    /// <summary>The downgraded projects whose failure is repository code or otherwise not environment-fixable.</summary>
    public IReadOnlyList<RemediationPlanItem> Unfixable =>
        Items.Where(i => !i.Loaded && (i.Signature is null || i.Signature.Remedy == "classify-only")).ToList();

    /// <summary>
    ///     The one-line workable-subset summary (for example
    ///     <c>6 of 8 projects oracle-grade; blockers: NU1507 on 2</c>), the header an agent reads to know what it
    ///     can trust before doing anything.
    /// </summary>
    public string WorkableSubsetLine
    {
        get
        {
            var blockers = Items
                .Where(i => !i.Loaded)
                .GroupBy(i => i.Signature?.Id ?? "unrecognized")
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key} on {g.Count()}")
                .ToList();
            var blockerText = blockers.Count == 0 ? "none" : string.Join(", ", blockers);
            return $"{ProjectsLoaded} of {ProjectsTotal} projects oracle-grade; blockers: {blockerText}";
        }
    }
}

/// <summary>
///     Turns a workspace load diagnosis into a remediation plan by classifying each downgraded project's failure
///     against the knowledge base (C1). This is the classify-and-report core of <c>fuse up</c>; it applies no
///     remedy and touches nothing, so it is safe to run before the remediation actions exist.
/// </summary>
public sealed class EnvironmentRemediationPlanner
{
    private readonly RemediationKnowledgeBase _knowledgeBase;

    /// <summary>
    ///     Initializes a new instance of the <see cref="EnvironmentRemediationPlanner" /> class.
    /// </summary>
    /// <param name="knowledgeBase">The knowledge base to classify failures against; defaults to the shipped default.</param>
    public EnvironmentRemediationPlanner(RemediationKnowledgeBase? knowledgeBase = null) =>
        _knowledgeBase = knowledgeBase ?? RemediationKnowledgeBase.LoadDefault();

    /// <summary>
    ///     Builds the remediation plan for a load diagnosis.
    /// </summary>
    /// <param name="diagnosis">The load diagnosis produced by the doctor ladder.</param>
    /// <returns>The plan: per-project classification plus the workable-subset summary.</returns>
    public RemediationPlan Plan(LoadDiagnosis diagnosis)
    {
        var items = new List<RemediationPlanItem>(diagnosis.Projects.Count);
        foreach (var project in diagnosis.Projects)
        {
            // A downgraded project's failure is classified by matching its loader reason against the knowledge base;
            // a loaded project needs no remedy. Only downgraded projects carry a signature.
            var signature = project.Loaded ? null : _knowledgeBase.Match(project.Reason);
            items.Add(new RemediationPlanItem(project.Name, project.Loaded, project.Reason, signature));
        }

        return new RemediationPlan(diagnosis.Tier, diagnosis.ProjectsLoaded, diagnosis.ProjectsTotal, items);
    }
}
