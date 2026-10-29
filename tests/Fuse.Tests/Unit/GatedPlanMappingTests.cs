using Fuse.Engine;
using Fuse.Protocol;
using Fuse.Testing.Model;

namespace Fuse.Tests.Unit;

/// <summary>A plan's gated suites cross the pipe: the mapper carries them and a line without them reads as none.</summary>
public class GatedPlanMappingTests
{
    [Fact]
    public void A_plan_result_maps_its_gated_suites_field_by_field()
    {
        var result = new TestPlanResult([], 0, 40, "ran 0 test(s) affected by your changes out of 40; skipped 5 test(s) in Gated.Tests (no container runtime); fuse test --all runs everything")
        {
            ContainerGatedTests = 5,
            ContainerGatedProjects = ["Gated.Tests"],
        };

        var plan = ResponseMapper.Plan(result);

        Assert.Equal(5, plan.ContainerGatedTests);
        Assert.Equal(["Gated.Tests"], plan.ContainerGatedProjects);
    }

    [Fact]
    public void Gated_suites_survive_the_pipe()
    {
        var result = new TestPlanResult([], 0, 40, "summary")
        {
            ContainerGatedTests = 5,
            ContainerGatedProjects = ["Gated.Tests"],
        };

        var line = ProtocolJson.Serialize(ResponseMapper.Answered(result));
        var plan = Assert.IsType<EngineResponse.PlanAnswered>(ProtocolJson.ReadResponse(line)).Plan;

        Assert.Equal(5, plan.ContainerGatedTests);
        Assert.Equal(["Gated.Tests"], plan.ContainerGatedProjects);
        Assert.Contains("\"containerGatedTests\":5", line, StringComparison.Ordinal);
        Assert.Contains("\"containerGatedProjects\":[\"Gated.Tests\"]", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_without_gated_suites_maps_to_none()
    {
        var plan = ResponseMapper.Plan(new TestPlanResult([], 0, 40, "summary"));

        Assert.Equal(0, plan.ContainerGatedTests);
        Assert.Empty(plan.ContainerGatedProjects);
    }

    [Fact]
    public void A_line_without_the_gated_fields_reads_them_as_none()
    {
        var plan = Assert.IsType<EngineResponse.PlanAnswered>(ProtocolJson.ReadResponse("""{"status":"PlanAnswered","plan":{"runs":[],"selectedTests":0,"totalTests":40,"summary":"summary"}}""")).Plan;

        Assert.Equal(0, plan.ContainerGatedTests);
        Assert.Empty(plan.ContainerGatedProjects);
    }
}
