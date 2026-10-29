using Fuse.Operations;
using Fuse.Protocol;

namespace Fuse.Tests.Unit;

/// <summary>
///     A summary that bound whole projects names the changed files that forced each project, capped with a pointer to
///     the engine log. An incremental summary reads as before.
/// </summary>
public class WholeProjectRenderTests
{
    [Fact]
    public void Whole_projects_name_their_forcing_files()
    {
        var report = new CheckReport([], 4, ["App", "Lib"], ["Lib"], 2, true, 4, 4, 0)
        {
            WholeProjects =
            [
                new ReportedWholeProject("App", ["Lib/Calc.cs"], ["removed: public int Add(int a, int b)"]),
                new ReportedWholeProject("Lib", ["Lib/Calc.cs"], ["removed: public int Add(int a, int b)"]),
            ],
        };

        var text = CheckOperation.Render(report).Text.Split('\n')[^1];

        Assert.Equal(
            "fuse: 4 error(s) introduced in 4 file(s) (App, Lib); Lib declarations changed, 2 dependent project(s) checked; checked whole projects: App because Lib/Calc.cs (removed: public int Add(int a, int b)); Lib because Lib/Calc.cs (removed: public int Add(int a, int b))",
            text);
    }

    [Fact]
    public void Whole_projects_without_causes_read_as_before()
    {
        var report = new CheckReport([], 4, ["App"], [], 0, true, 1, 1, 0);

        var text = CheckOperation.Render(report).Text.Split('\n')[^1];

        Assert.Contains("checked whole projects", text, StringComparison.Ordinal);
        Assert.DoesNotContain("because", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Past_the_caps_the_summary_points_to_the_engine_log()
    {
        var report = new CheckReport([], 10, ["P"], [], 0, true, 1, 1, 0)
        {
            WholeProjects =
            [
                new ReportedWholeProject("A", ["F1.cs", "F2.cs", "F3.cs"], ["removed: a"]),
                new ReportedWholeProject("B", ["G.cs"], ["removed: b"]),
                new ReportedWholeProject("C", ["H.cs"], ["removed: c"]),
                new ReportedWholeProject("D", ["I.cs"], ["removed: d"]),
            ],
        };

        var text = CheckOperation.Render(report).Text.Split('\n')[^1];

        Assert.Contains("checked whole projects: A because F1.cs, F2.cs (removed: a); B because G.cs (removed: b); C because H.cs (removed: c); and 2 more in the engine log", text, StringComparison.Ordinal);
    }
}
