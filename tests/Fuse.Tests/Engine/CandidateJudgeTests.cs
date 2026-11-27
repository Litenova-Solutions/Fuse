using Fuse.Engine;
using Fuse.Failures;
using Fuse.Operations.Judge;
using Fuse.Protocol.Requests;
using Fuse.Tests.Fixtures;
namespace Fuse.Tests.Engine;

public class CandidateJudgeTests
{
    [Fact]
    public async Task Race_prefers_a_clean_candidate_over_one_that_breaks_a_call_site()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var clean = PatchFor(engine, "Lib/Calc.cs", "a * b;", "a * b + 0;");
        var breaking = PatchFor(engine, "Lib/Calc.cs", "public int Add(", "public int Plus(");
        var service = new CandidateJudge(engine.Workspace, engine.Repo.Root);
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
        var service = new CandidateJudge(engine.Workspace, engine.Repo.Root);
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
        var service = new CandidateJudge(engine.Workspace, engine.Repo.Root);
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

        var service = new CandidateJudge(engine.Workspace, engine.Repo.Root);
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

    [Fact]
    public async Task A_candidate_that_touches_no_source_file_conflicts_and_wins_nothing()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var notes = "diff --git a/docs/notes.md b/docs/notes.md\nnew file mode 100644\n--- /dev/null\n+++ b/docs/notes.md\n@@ -0,0 +1 @@\n+hello\n";
        var service = new CandidateJudge(engine.Workspace, engine.Repo.Root);
        var report = await service.RunAsync(
            new JudgeRequest(JudgeRequest.JudgeMode.Race, [new JudgeRequest.Candidate("notes", notes)]),
            TestContext.Current.CancellationToken);

        Assert.Equal("race", report.Mode);
        Assert.Null(report.Winner);
        Assert.False(report.Clean);
        var verdict = Assert.Single(report.Verdicts);
        Assert.False(verdict.Compiles);
        Assert.Contains("no source file", verdict.ConflictReason, StringComparison.Ordinal);
        Assert.Equal("", FixtureRepo.Run(engine.Repo.Root.Path, "git", "status", "--porcelain"));
    }

    [Fact]
    public async Task Too_many_or_too_large_candidates_are_refused()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var service = new CandidateJudge(engine.Workspace, engine.Repo.Root);
        var tiny = "diff --git a/f.cs b/f.cs\n--- a/f.cs\n+++ b/f.cs\n@@ -1 +1 @@\n-a\n+b\n";

        var many = Enumerable.Range(0, 17).Select(i => new JudgeRequest.Candidate($"c{i}", tiny)).ToArray();
        var countError = await Assert.ThrowsAsync<FuseException>(() => service.RunAsync(
            new JudgeRequest(JudgeRequest.JudgeMode.Race, many),
            TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCode.InvalidPath, countError.Code);
        Assert.Contains("more than the 16", countError.Message, StringComparison.Ordinal);

        var big = new string('x', 1024 * 1024 + 1);
        var sizeError = await Assert.ThrowsAsync<FuseException>(() => service.RunAsync(
            new JudgeRequest(JudgeRequest.JudgeMode.Race, [new JudgeRequest.Candidate("big", big)]),
            TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCode.InvalidPath, sizeError.Code);
        Assert.Contains("more than the 1 MB", sizeError.Message, StringComparison.Ordinal);

        var chunk = new string('y', 900 * 1024);
        var heavy = Enumerable.Range(0, 10).Select(i => new JudgeRequest.Candidate($"t{i}", chunk)).ToArray();
        var totalError = await Assert.ThrowsAsync<FuseException>(() => service.RunAsync(
            new JudgeRequest(JudgeRequest.JudgeMode.Race, heavy),
            TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCode.InvalidPath, totalError.Code);
        Assert.Contains("more than the 8 MB", totalError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_untracked_diff_keeps_crlf_and_marks_a_missing_trailing_newline()
    {
        using var repo = FixtureRepo.CreateStandard();
        // Written directly: FixtureRepo.Write normalizes line endings to LF, and this test needs the CRLF bytes.
        File.WriteAllText(Path.Combine(repo.Path, "crlf.txt"), "a\r\nb\r\n");
        repo.Write("noeol.txt", "line1\nline2");
        try
        {
            var (candidate, refusal) = await JudgeOperation.LoadCandidateAsync(repo.Root.Path, repo.Root.Path, TestContext.Current.CancellationToken);
            Assert.Null(refusal);
            Assert.NotNull(candidate);
            Assert.Contains("+a\r\n", candidate.Diff, StringComparison.Ordinal);
            Assert.Contains("\\ No newline at end of file", candidate.Diff, StringComparison.Ordinal);

            var (files, parseError) = UnifiedDiff.Parse(candidate.Diff);
            Assert.Null(parseError);
            var noeol = files.Single(f => f.Path == "noeol.txt");
            var (text, error) = UnifiedDiff.ApplyToText("", noeol);
            Assert.Null(error);
            Assert.Equal("line1\nline2", text);
        }
        finally
        {
            repo.Delete("crlf.txt");
            repo.Delete("noeol.txt");
        }
    }

    [Fact]
    public async Task Combine_applies_a_hunk_below_one_that_added_lines()
    {
        await using var engine = await InProcessEngine.StartAsync(WithSteps());
        // The first candidate adds a line, so the second one's hunk sits a line lower once the first is applied.
        var first = PatchFor(engine, "Lib/Steps.cs", "public int Step1() => 1;", "public int Step1() => 1;\n    public int Step0() => 0;");
        var second = PatchFor(engine, "Lib/Steps.cs", "public int Step30() => 30;", "public int Step30() => 30 + 0;");
        var service = new CandidateJudge(engine.Workspace, engine.Repo.Root);
        var report = await service.RunAsync(
            new JudgeRequest(JudgeRequest.JudgeMode.Combine, [new JudgeRequest.Candidate("first", first), new JudgeRequest.Candidate("second", second)]),
            TestContext.Current.CancellationToken);

        Assert.Empty(report.Conflicts);
        Assert.True(report.Clean);
        Assert.All(report.Verdicts, v => Assert.Null(v.ConflictReason));
    }

    [Fact]
    public async Task A_diff_that_drops_the_byte_order_mark_still_applies()
    {
        await using var engine = await InProcessEngine.StartAsync(WithSteps(byteOrderMark: true));
        var path = Path.Combine(engine.Repo.Root.Path, "Lib", "Steps.cs");
        var original = File.ReadAllBytes(path);
        // .NET's File.WriteAllText, like many editors, writes UTF-8 without the mark the file had.
        File.WriteAllText(path, File.ReadAllText(path).Replace("public int Step1() => 1;", "public int Step1() => 1 + 0;", StringComparison.Ordinal));
        var diff = FixtureRepo.Run(engine.Repo.Root.Path, "git", "diff", "--no-color", "--", "Lib/Steps.cs");
        File.WriteAllBytes(path, original);
        var service = new CandidateJudge(engine.Workspace, engine.Repo.Root);
        var report = await service.RunAsync(
            new JudgeRequest(JudgeRequest.JudgeMode.Race, [new JudgeRequest.Candidate("clean", diff)]),
            TestContext.Current.CancellationToken);

        Assert.Equal("clean", report.Winner);
        Assert.Empty(report.Conflicts);
    }

    /// <summary>The standard repository with a committed <c>Lib/Steps.cs</c> of 30 one-line methods.</summary>
    private static FixtureRepo WithSteps(bool byteOrderMark = false)
    {
        var repo = FixtureRepo.CreateStandard();
        var methods = string.Join("\n", Enumerable.Range(1, 30).Select(n => $"    public int Step{n}() => {n};"));
        var text = $"namespace Lib;\n\npublic class Steps\n{{\n{methods}\n}}\n";
        File.WriteAllText(Path.Combine(repo.Path, "Lib", "Steps.cs"), text, new System.Text.UTF8Encoding(byteOrderMark));
        repo.Commit("add steps");
        return repo;
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
