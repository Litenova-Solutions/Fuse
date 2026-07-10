using Fuse.Semantics;
using Fuse.Semantics.Remediation;
using Xunit;

namespace Fuse.Semantics.Tests;

// C1: the machine-readable fuse up report (--json). UpRepoReport.From flattens a plan and UpReportJson.Serialize
// emits the source-generated JSON, the shape workspace status reads and the up-report harness consolidates.
// Deterministic over a synthetic plan.
public sealed class UpReportJsonTests
{
    private static RemediationPlan Plan(params ProjectLoadReport[] projects) =>
        new EnvironmentRemediationPlanner().Plan(new LoadDiagnosis(
            Tier: "partial",
            ProjectsLoaded: projects.Count(p => p.Loaded),
            ProjectsTotal: projects.Length,
            Projects: projects,
            Diagnostics: new List<DiagnosticRecord>()));

    [Fact]
    public void Serialize_carries_tier_workable_line_and_flattened_per_project_remedy()
    {
        var plan = Plan(
            new ProjectLoadReport("Api", "Api.csproj", Loaded: true, Reason: "loaded"),
            new ProjectLoadReport("Data", "Data.csproj", Loaded: false, Reason: "error NU1507: multiple sources"));

        var json = UpReportJson.Serialize(new UpResult("C:/repo", Applied: false, UpRepoReport.From(plan), After: null));

        Assert.Contains("\"root\": \"C:/repo\"", json);
        Assert.Contains("\"applied\": false", json);
        Assert.Contains("\"tier\": \"partial\"", json);
        Assert.Contains("1 of 2 projects oracle-grade", json);
        // The NU1507 project is flattened with its remedy id and key.
        Assert.Contains("\"remedyId\": \"NU1507\"", json);
        Assert.Contains("\"remedy\": \"overlay-nuget-source-mapping\"", json);
        Assert.Contains("\"remediable\": 1", json);
        // No after-plan when nothing was applied.
        Assert.Contains("\"after\": null", json);
    }

    [Fact]
    public void Serialize_carries_the_after_plan_when_a_remedy_was_applied()
    {
        var before = Plan(new ProjectLoadReport("Data", "Data.csproj", Loaded: false, Reason: "error NU1507: multiple sources"));
        var after = Plan(new ProjectLoadReport("Data", "Data.csproj", Loaded: true, Reason: "loaded"));

        var json = UpReportJson.Serialize(new UpResult("C:/repo", Applied: true, UpRepoReport.From(before), UpRepoReport.From(after)));

        Assert.Contains("\"applied\": true", json);
        // The after block reports the project loaded (tier re-attempted).
        Assert.Contains("\"after\":", json);
        Assert.Contains("\"loaded\": true", json);
    }
}
