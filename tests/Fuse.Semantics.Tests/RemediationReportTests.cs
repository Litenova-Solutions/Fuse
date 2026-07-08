using Fuse.Semantics;
using Fuse.Semantics.Remediation;
using Xunit;

namespace Fuse.Semantics.Tests;

// C1: the fuse up report renderer. Given a plan, it prints the tier, the workable-subset line, and a per-project
// line naming the remedy (or that the failure is repository code / unrecognized). Pure and deterministic, tested
// over a synthetic plan.
public sealed class RemediationReportTests
{
    private static RemediationPlan Plan(params ProjectLoadReport[] projects)
    {
        var planner = new EnvironmentRemediationPlanner();
        return planner.Plan(new LoadDiagnosis(
            Tier: "partial",
            ProjectsLoaded: projects.Count(p => p.Loaded),
            ProjectsTotal: projects.Length,
            Projects: projects,
            Diagnostics: new List<DiagnosticRecord>()));
    }

    [Fact]
    public void Render_names_the_tier_workable_line_and_per_project_remedies()
    {
        var report = RemediationReport.Render(Plan(
            new ProjectLoadReport("Api", "Api.csproj", Loaded: true, Reason: "loaded"),
            new ProjectLoadReport("Data", "Data.csproj", Loaded: false, Reason: "error NU1507: multiple sources"),
            new ProjectLoadReport("Sdk", "Sdk.csproj", Loaded: false, Reason: "error NETSDK1045: unsupported"),
            new ProjectLoadReport("Legacy", "Legacy.csproj", Loaded: false, Reason: "error CS0104: ambiguous")));

        Assert.Contains("load tier: partial", report);
        Assert.Contains("1 of 4 projects oracle-grade", report);
        Assert.Contains("[ok] Api", report);
        Assert.Contains("remedy: overlay-nuget-source-mapping", report);
        // The SDK remedy needs consent.
        Assert.Contains("remedy: install-sdk (needs --allow-install)", report);
        // Repository code is named as not fixable by fuse up.
        Assert.Contains("repository code, not fixable by fuse up", report);
        Assert.Contains("remediable: 2; unfixable by fuse up: 1", report);
    }

    [Fact]
    public void Render_marks_unrecognized_failures_as_no_known_remedy()
    {
        var report = RemediationReport.Render(Plan(
            new ProjectLoadReport("Mystery", "Mystery.csproj", Loaded: false, Reason: "an error the KB does not know")));

        Assert.Contains("no known remedy", report);
        Assert.Contains("remediable: 0; unfixable by fuse up: 1", report);
    }
}
