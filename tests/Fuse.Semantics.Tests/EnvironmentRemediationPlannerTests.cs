using Fuse.Semantics;
using Fuse.Semantics.Remediation;
using Xunit;

namespace Fuse.Semantics.Tests;

// C1: the classify-and-report core of fuse up. Given a load diagnosis (as the doctor ladder produces), the
// planner classifies each downgraded project against the knowledge base and produces the workable-subset line an
// agent reads at minute zero. These tests use a synthetic diagnosis so they are deterministic and need no build.
public sealed class EnvironmentRemediationPlannerTests
{
    private static LoadDiagnosis Diagnosis(params ProjectLoadReport[] projects) =>
        new(
            Tier: "partial",
            ProjectsLoaded: projects.Count(p => p.Loaded),
            ProjectsTotal: projects.Length,
            Projects: projects,
            Diagnostics: new List<DiagnosticRecord>());

    [Fact]
    public void Plan_classifies_downgraded_projects_and_leaves_loaded_ones_unmarked()
    {
        var planner = new EnvironmentRemediationPlanner();
        var plan = planner.Plan(Diagnosis(
            new ProjectLoadReport("Api", "Api.csproj", Loaded: true, Reason: "loaded"),
            new ProjectLoadReport("Data", "Data.csproj", Loaded: false, Reason: "restore failed: error NU1507: multiple sources"),
            new ProjectLoadReport("Legacy", "Legacy.csproj", Loaded: false, Reason: "error CS0104: ambiguous reference")));

        var api = plan.Items.Single(i => i.Project == "Api");
        Assert.True(api.Loaded);
        Assert.Null(api.Signature);

        var data = plan.Items.Single(i => i.Project == "Data");
        Assert.Equal("NU1507", data.Signature!.Id);
        Assert.Equal("overlay-nuget-source-mapping", data.Signature.Remedy);

        var legacy = plan.Items.Single(i => i.Project == "Legacy");
        Assert.Equal("CS0104", legacy.Signature!.Id);
        Assert.Equal("classify-only", legacy.Signature.Remedy);
    }

    [Fact]
    public void Remediable_and_unfixable_partition_the_downgraded_projects()
    {
        var planner = new EnvironmentRemediationPlanner();
        var plan = planner.Plan(Diagnosis(
            new ProjectLoadReport("Data", "Data.csproj", Loaded: false, Reason: "error NU1507: multiple sources"),
            new ProjectLoadReport("Legacy", "Legacy.csproj", Loaded: false, Reason: "error CS2007: bad option"),
            new ProjectLoadReport("Mystery", "Mystery.csproj", Loaded: false, Reason: "something the KB does not know")));

        Assert.Equal(["Data"], plan.Remediable.Select(i => i.Project).ToArray());
        // Legacy is classify-only (repo code); Mystery is unrecognized; both are unfixable by fuse up.
        Assert.Equal(["Legacy", "Mystery"], plan.Unfixable.Select(i => i.Project).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Workable_subset_line_summarizes_loaded_count_and_blockers()
    {
        var planner = new EnvironmentRemediationPlanner();
        var plan = planner.Plan(Diagnosis(
            new ProjectLoadReport("A", "A.csproj", Loaded: true, Reason: "loaded"),
            new ProjectLoadReport("B", "B.csproj", Loaded: true, Reason: "loaded"),
            new ProjectLoadReport("C", "C.csproj", Loaded: false, Reason: "error NU1507: multiple sources"),
            new ProjectLoadReport("D", "D.csproj", Loaded: false, Reason: "error NU1507: multiple sources")));

        Assert.Equal("2 of 4 projects oracle-grade; blockers: NU1507 on 2", plan.WorkableSubsetLine);
    }

    [Fact]
    public void Fully_loaded_workspace_has_no_blockers()
    {
        var planner = new EnvironmentRemediationPlanner();
        var plan = planner.Plan(Diagnosis(
            new ProjectLoadReport("A", "A.csproj", Loaded: true, Reason: "loaded")));

        Assert.Equal("1 of 1 projects oracle-grade; blockers: none", plan.WorkableSubsetLine);
        Assert.Empty(plan.Remediable);
        Assert.Empty(plan.Unfixable);
    }
}
