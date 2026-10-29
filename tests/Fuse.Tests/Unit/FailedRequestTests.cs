using Fuse.Protocol;

namespace Fuse.Tests.Unit;

public class FailedRequestTests
{
    [Fact]
    public void A_failed_tests_plan_names_its_case_first_and_reads_back_equal()
    {
        var request = new EngineRequest.PlanFailedTests(
        [
            new EngineRequest.FailedProject("/repo/Lib.Tests/Lib.Tests.csproj", ["Lib.Tests.CalcTests.Multiplies", "Lib.Tests.CalcTests.Adds"]),
        ])
        { BuildId = "5.7.0/abc" };

        var line = ProtocolJson.Serialize(request);
        var read = ProtocolJson.ReadRequest(line);

        Assert.StartsWith("""{"request":"PlanFailedTests",""", line, StringComparison.Ordinal);
        Assert.Equal(request, read);
        var plan = Assert.IsType<EngineRequest.PlanFailedTests>(read);
        Assert.Equal(["Lib.Tests.CalcTests.Multiplies", "Lib.Tests.CalcTests.Adds"], plan.Projects[0].Tests);
    }

    [Fact]
    public void A_line_without_the_lists_reads_them_as_empty()
    {
        var plan = Assert.IsType<EngineRequest.PlanFailedTests>(ProtocolJson.ReadRequest("""{"request":"PlanFailedTests"}"""));

        Assert.Empty(plan.Projects);
    }
}
