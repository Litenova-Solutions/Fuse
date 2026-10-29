using Fuse.Dotnet;
using Fuse.Operations;
using Fuse.Protocol;

namespace Fuse.Tests.Unit;

/// <summary>What <c>fuse test</c> prints when a container runtime gates suites: nothing ran reads as blocked, and a partial plan names the skipped projects.</summary>
public class GatedTestRenderTests
{
    [Fact]
    public void Every_test_gated_is_no_test_ran_not_a_pass()
    {
        var plan = new TestPlan([], 0, 40, "ran 0 test(s) affected by your changes out of 40; skipped 5 test(s) in NightHall.Acceptance.Tests (no container runtime); fuse test --all runs everything")
        {
            ContainerGatedTests = 5,
            ContainerGatedProjects = ["NightHall.Acceptance.Tests"],
        };

        var result = TestOperation.WithoutRuntime(plan);

        Assert.Equal(Outcome.ProblemsFound, result.Outcome);
        Assert.Equal("fuse: no test ran: 5 test(s) selected in NightHall.Acceptance.Tests need a container runtime, and none is running; start Docker or Podman and run fuse test again", result.Text);
    }

    [Fact]
    public void Several_gated_projects_are_named_in_order()
    {
        var plan = new TestPlan([], 0, 40, "summary")
        {
            ContainerGatedTests = 9,
            ContainerGatedProjects = ["A.Tests", "B.Tests"],
        };

        Assert.Equal(
            "fuse: no test ran: 9 test(s) selected in A.Tests, B.Tests need a container runtime, and none is running; start Docker or Podman and run fuse test again",
            TestOperation.WithoutRuntime(plan).Text);
    }

    [Fact]
    public void A_partial_plan_keeps_the_gated_fragment_and_counts_no_gated_test_as_passed()
    {
        var outcome = new TrxResults(12, 0, 3, []);

        var result = TestOperation.Render(outcome, "ran 12 test(s) affected by your changes out of 40; skipped 5 test(s) in NightHall.Acceptance.Tests (no container runtime); fuse test --all runs everything; without MSBuild", 2.1);

        Assert.Equal(Outcome.Clean, result.Outcome);
        Assert.Equal("fuse: 0 failed, 12 passed, 3 skipped in 2.1 s; ran 12 test(s) affected by your changes out of 40; skipped 5 test(s) in NightHall.Acceptance.Tests (no container runtime); fuse test --all runs everything; without MSBuild", result.Text);
    }
}
