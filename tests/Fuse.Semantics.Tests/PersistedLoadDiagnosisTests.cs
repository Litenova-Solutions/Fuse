using System.Text.Json;
using Fuse.Indexing;
using Fuse.Semantics;
using Xunit;

namespace Fuse.Semantics.Tests;

// R43: the load diagnosis stamped into the index reflects what was actually indexed, and round-trips through its
// source-generated JSON context. Pure (no SDK): builds the diagnosis from a synthetic capture and snapshot.
public sealed class PersistedLoadDiagnosisTests
{
    private static WorkspaceDiscoveryResult SolutionDiscovery(string? note = null) =>
        new(WorkspaceKind.Solution, "/repo/App.sln", [], "/repo", note);

    [Fact]
    public void Capture_diagnosis_maps_projects_and_is_oracle_grade_when_all_clean()
    {
        var capture = CaptureResult.Ok(
        [
            new CapturedProject("A", "/repo/A/A.csproj", "A", ErrorCount: 0, TypeCount: 3),
            new CapturedProject("B", "/repo/B/B.csproj", "B", ErrorCount: 0, TypeCount: 5),
        ]);

        var diagnosis = SemanticIndexer.BuildDiagnosisFromCapture(SolutionDiscovery(), capture);

        Assert.Equal("oracle-grade (all projects loaded clean)", diagnosis.Tier);
        Assert.Equal(2, diagnosis.ProjectsLoaded);
        Assert.Equal(2, diagnosis.ProjectsTotal);
        Assert.All(diagnosis.Projects, p => Assert.True(p.Loaded));
        Assert.All(diagnosis.Projects, p => Assert.Equal("loaded", p.Reason));
        Assert.Equal("/repo/App.sln", diagnosis.SelectedSolution);
    }

    [Fact]
    public void Capture_diagnosis_is_graph_grade_when_a_project_has_errors()
    {
        var capture = CaptureResult.Ok(
        [
            new CapturedProject("A", "/repo/A/A.csproj", "A", ErrorCount: 0, TypeCount: 3),
            new CapturedProject("B", "/repo/B/B.csproj", "B", ErrorCount: 2, TypeCount: 5),
        ]);

        var diagnosis = SemanticIndexer.BuildDiagnosisFromCapture(SolutionDiscovery(), capture);

        Assert.Equal("graph-grade (partial)", diagnosis.Tier);
        var b = diagnosis.Projects.Single(p => p.Name == "B");
        Assert.Contains("compile errors", b.Reason);
    }

    [Fact]
    public void Tier_is_syntax_when_no_projects()
    {
        Assert.Equal("syntax", SemanticIndexer.ComputeTier(semanticLoadSucceeded: false, loaded: 0, total: 0, anyErrors: false));
        Assert.Equal("graph-grade (partial)", SemanticIndexer.ComputeTier(true, loaded: 1, total: 2, anyErrors: false));
        Assert.Equal("oracle-grade (all projects loaded clean)", SemanticIndexer.ComputeTier(true, loaded: 2, total: 2, anyErrors: false));
    }

    [Fact]
    public void Persisted_diagnosis_round_trips_through_its_source_generated_context()
    {
        var original = new PersistedLoadDiagnosis(
            "graph-grade (partial)",
            ProjectsLoaded: 1,
            ProjectsTotal: 2,
            Projects:
            [
                new PersistedProjectReport("A", "/repo/A/A.csproj", true, "loaded"),
                new PersistedProjectReport("B", "/repo/B/B.csproj", false, "no compilation (project unrestored, SDK mismatch, or a build error)"),
            ],
            SelectedSolution: "/repo/App.sln",
            SelectionNote: "selected a fixture solution");

        var json = JsonSerializer.Serialize(original, PersistedLoadDiagnosisJsonContext.Default.PersistedLoadDiagnosis);
        var restored = JsonSerializer.Deserialize(json, PersistedLoadDiagnosisJsonContext.Default.PersistedLoadDiagnosis);

        Assert.NotNull(restored);
        Assert.Equal(original.Tier, restored!.Tier);
        Assert.Equal(original.ProjectsLoaded, restored.ProjectsLoaded);
        Assert.Equal(original.ProjectsTotal, restored.ProjectsTotal);
        Assert.Equal(original.SelectedSolution, restored.SelectedSolution);
        Assert.Equal(original.SelectionNote, restored.SelectionNote);
        Assert.Equal(original.Projects.Count, restored.Projects.Count);
        Assert.Equal(original.Projects[1].Reason, restored.Projects[1].Reason);
    }
}
