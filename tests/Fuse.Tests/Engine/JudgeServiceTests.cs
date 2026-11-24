using Fuse.Engine;
using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

public class JudgeServiceTests
{
    [Fact]
    public async Task Race_prefers_a_clean_candidate_over_one_that_breaks_a_call_site()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var clean = PatchFor(engine, "Lib/Calc.cs", "a * b;", "a * b + 0;");
        var breaking = PatchFor(engine, "Lib/Calc.cs", "public int Add(", "public int Plus(");
        var service = new JudgeService(engine.Workspace, engine.Repo.Root);
        var report = await service.RunAsync(
            new JudgeRequest(JudgeRequest.JudgeMode.Race, [new JudgeRequest.Candidate("clean", clean), new JudgeRequest.Candidate("breaking", breaking)]),
            TestContext.Current.CancellationToken);

        Assert.Equal("race", report.Mode);
        Assert.Equal("clean", report.Winner);
        Assert.True(report.Clean);
        Assert.Empty(report.Conflicts);
        var ranked = report.Verdicts.OrderBy(v => v.Rank).ToList();
        Assert.Equal("clean", ranked[0].Name);
        Assert.True(ranked[0].Compiles);
        Assert.Equal(0, ranked[0].ErrorCount);
        Assert.Equal("breaking", ranked[1].Name);
        Assert.False(ranked[1].Compiles);
        Assert.Contains(ranked[1].Errors, e => e.Error.Id == "CS1061" && e.Error.Path == "App/Program.cs");
        Assert.True(ranked[1].SurfaceBreaks > 0);

        Assert.Contains("a * b;", engine.Repo.Read("Lib/Calc.cs"), StringComparison.Ordinal);
        Assert.Equal("", FixtureRepo.Run(engine.Repo.Root.Path, "git", "status", "--porcelain"));
    }

    [Fact]
    public async Task Combine_reports_overlapping_candidates_as_a_conflict()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var first = PatchFor(engine, "Lib/Calc.cs", "public int Add(", "public int Plus(");
        var second = PatchFor(engine, "Lib/Calc.cs", "public int Add(", "public int Sum(");
        var service = new JudgeService(engine.Workspace, engine.Repo.Root);
        var report = await service.RunAsync(
            new JudgeRequest(JudgeRequest.JudgeMode.Combine, [new JudgeRequest.Candidate("first", first), new JudgeRequest.Candidate("second", second)]),
            TestContext.Current.CancellationToken);

        Assert.Equal("combine", report.Mode);
        Assert.Null(report.Winner);
        Assert.False(report.Clean);
        var conflict = Assert.Single(report.Conflicts);
        Assert.Contains("second", conflict, StringComparison.Ordinal);
        var ranked = report.Verdicts.OrderBy(v => v.Rank).ToList();
        Assert.Equal(2, ranked.Count);
        Assert.Null(ranked[0].ConflictReason);
        Assert.True(ranked[0].ErrorCount > 0);
        Assert.Contains("first", ranked[1].ConflictReason, StringComparison.Ordinal);
        Assert.Equal("", FixtureRepo.Run(engine.Repo.Root.Path, "git", "status", "--porcelain"));
    }

    [Fact]
    public async Task Head_errors_are_not_counted_against_a_candidate()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * existingError;");
        engine.Repo.Commit("commit a broken file");
        var clean = PatchFor(engine, "Lib/Calc.cs", "=> a + b;", "=> a + b + 0;");
        var service = new JudgeService(engine.Workspace, engine.Repo.Root);
        var report = await service.RunAsync(
            new JudgeRequest(JudgeRequest.JudgeMode.Race, [new JudgeRequest.Candidate("clean", clean)]),
            TestContext.Current.CancellationToken);

        var verdict = Assert.Single(report.Verdicts);
        Assert.True(verdict.Compiles);
        Assert.Equal(0, verdict.ErrorCount);
        Assert.Equal("clean", report.Winner);
    }

    [Fact]
    public async Task Added_file_with_an_error_is_reported_without_writing_the_tree()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Write("Lib/Extra.cs", "namespace Lib;\n\npublic static class Extra\n{\n    public static int Value() => missing;\n}\n");
        FixtureRepo.Run(engine.Repo.Root.Path, "git", "add", "-N", "--", "Lib/Extra.cs");
        var diff = FixtureRepo.Run(engine.Repo.Root.Path, "git", "diff", "--no-color", "--", "Lib/Extra.cs");
        FixtureRepo.Run(engine.Repo.Root.Path, "git", "reset", "-q", "--", "Lib/Extra.cs");
        engine.Repo.Delete("Lib/Extra.cs");
        Assert.Contains("public static class Extra", diff, StringComparison.Ordinal);

        var service = new JudgeService(engine.Workspace, engine.Repo.Root);
        var report = await service.RunAsync(
            new JudgeRequest(JudgeRequest.JudgeMode.Race, [new JudgeRequest.Candidate("added", diff)]),
            TestContext.Current.CancellationToken);

        var verdict = Assert.Single(report.Verdicts);
        Assert.False(verdict.Compiles);
        var error = Assert.Single(verdict.Errors).Error;
        Assert.Equal("Lib/Extra.cs", error.Path);
        Assert.Equal("CS0103", error.Id);
        Assert.False(File.Exists(engine.Repo.Full("Lib/Extra.cs")));
        Assert.Equal("", FixtureRepo.Run(engine.Repo.Root.Path, "git", "status", "--porcelain"));
    }

    /// <summary>The git diff of replacing oldText with newText in the file, with the file restored after.</summary>
    private static string PatchFor(InProcessEngine engine, string relative, string oldText, string newText)
    {
        engine.Repo.Replace(relative, oldText, newText);
        try
        {
            return FixtureRepo.Run(engine.Repo.Root.Path, "git", "diff", "--no-color", "--", relative);
        }
        finally
        {
            engine.Repo.Replace(relative, newText, oldText);
        }
    }
}
