using Fuse.Check.Model;
using Fuse.Engine;
using Fuse.Protocol;

namespace Fuse.Tests.Unit;

/// <summary>
///     A check's scope and its whole-project causes cross the pipe the way the summary prints them: the scoped paths in
///     the order named, and each whole project with the files and causes that forced it.
/// </summary>
public class WholeProjectMapperTests
{
    [Fact]
    public void Scoped_paths_and_whole_projects_map_field_by_field()
    {
        var result = new CheckResult([], 3, [], [], 0, true, 0, 0, 0)
        {
            ScopedPaths = ["Lib", "App/*.cs"],
            WholeProjectCauses =
            [
                new WholeProjectCause("App", ["Lib/Calc.cs"], ["removed: public int Add(int a, int b)"]),
                new WholeProjectCause("Lib", ["Lib/Calc.cs"], ["removed: public int Add(int a, int b)"]),
            ],
        };

        var report = ResponseMapper.Report(result);

        Assert.Equal(["Lib", "App/*.cs"], report.ScopedPaths);
        Assert.Equal(["App", "Lib"], report.WholeProjects.Select(w => w.Project));
        Assert.Equal(["Lib/Calc.cs"], report.WholeProjects[0].ChangedFiles);
        Assert.Equal(["removed: public int Add(int a, int b)"], report.WholeProjects[0].Causes);
    }

    [Fact]
    public void An_unscoped_incremental_report_maps_to_empty()
    {
        var report = ResponseMapper.Report(new CheckResult([], 1, [], [], 0, false, 0, 0, 0));

        Assert.Empty(report.ScopedPaths);
        Assert.Empty(report.WholeProjects);
    }

    [Fact]
    public void Scoped_paths_and_whole_projects_cross_the_pipe()
    {
        var result = new CheckResult([], 3, [], [], 0, true, 0, 0, 0)
        {
            ScopedPaths = ["Lib"],
            WholeProjectCauses = [new WholeProjectCause("Lib", ["Lib/Calc.cs"], ["removed: public int Add(int a, int b)"])],
        };

        var line = ProtocolJson.Serialize(ResponseMapper.Answered(result));
        var report = Assert.IsType<EngineResponse.CheckAnswered>(ProtocolJson.ReadResponse(line)).Report;

        Assert.Equal(["Lib"], report.ScopedPaths);
        var whole = Assert.Single(report.WholeProjects);
        Assert.Equal("Lib", whole.Project);
        Assert.Equal("\"scopedPaths\"", LineName(line, "scopedPaths"));
        Assert.Equal("\"wholeProjects\"", LineName(line, "wholeProjects"));
    }

    private static string LineName(string line, string name)
    {
        var quoted = $"\"{name}\"";
        Assert.Contains(quoted, line, StringComparison.Ordinal);
        return quoted;
    }
}
