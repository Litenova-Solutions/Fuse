using Fuse.Check;
using Fuse.Check.Model;
using Fuse.Engine;
using Fuse.Operations;
using Fuse.Paths;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>Every candidate error carries its cause, so errors sharing one declaration print as one group.</summary>
public class GroupedCausesTests
{
    private static readonly RepoRoot Root = FixtureRepo.CheckoutRoot;

    [Fact]
    public void Twenty_five_errors_sharing_one_cause_print_as_one_group()
    {
        var causes = new Dictionary<RepoPath, Cause>();
        var errors = new List<CompilerError>();
        for (var i = 0; i < 25; i++)
        {
            var path = $"src/Candidate{i}.cs";
            causes[Root.PathOf(path)] = new Cause.Removed("public class Calc");
            errors.Add(new(path, 1, 1, "CS1061", "no definition"));
        }

        var attached = CauseLines.Attach(errors, new Reach.Precise(causes), Root, []);
        Assert.All(attached, e => Assert.NotNull(e.Cause));

        var report = ResponseMapper.Report(new CheckResult(attached, 25, ["P"], [], 0, false, 25, 25, 0));
        var split = CheckGroups.Split(report.Errors, 20);

        Assert.Equal(2, split.Count);
        Assert.NotNull(split[0].Header);
        Assert.Contains("(25 error(s), first 2 shown)", split[0].Header);
        Assert.All(split, e => Assert.False(e.ShowCause));
    }

    [Fact]
    public void The_eleventh_cause_line_is_left_out_and_counted()
    {
        var introduced = Enumerable.Range(0, 12).Select(i => new IntroducedError(
            new CompilerError($"src/C{i}.cs", 1, 1, "CS1061", "no definition"),
            new Cause.Changed($"public int Method{i}()"))).ToList();
        var text = CheckOperation.Render(ResponseMapper.Report(new CheckResult(introduced, 12, ["P"], [], 0, false, 12, 12, 0))).Text;

        Assert.Equal(10, text.Split('\n').Count(l => l.StartsWith("  changed: ", StringComparison.Ordinal)));
        Assert.Contains("2 cause(s) left out", text);
    }
}
