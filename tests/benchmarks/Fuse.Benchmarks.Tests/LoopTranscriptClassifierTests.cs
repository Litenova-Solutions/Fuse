using Fuse.Benchmarks;
using Xunit;

namespace Fuse.Benchmarks.Tests;

// R4: the deterministic core of the loop suite. The classifier maps a stream-json task-resolution transcript
// to the ordered turns LoopMetrics counts, so the loop claim is measured (build-gated turns to green) rather
// than asserted. These tests pin the turn classification and the pass/fail read from each tool_result.
public sealed class LoopTranscriptClassifierTests
{
    // A minimal stream-json session: read, edit, a failing build, another edit, a passing build.
    private const string Transcript = """
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Read","input":{"file_path":"/w/Order.cs"}}]}}
        {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"file contents"}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Edit","input":{"file_path":"/w/Order.cs"}}]}}
        {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t2","content":"ok"}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"t3","name":"Bash","input":{"command":"dotnet build"}}]}}
        {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t3","content":"Order.cs(5): error CS1061: no member"}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"t4","name":"Edit","input":{"file_path":"/w/Order.cs"}}]}}
        {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t4","content":"ok"}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"t5","name":"Bash","input":{"command":"dotnet build -c Release"}}]}}
        {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t5","content":"Build succeeded. 0 Error(s)"}]}}
        """;

    [Fact]
    public void Classifies_reads_edits_and_builds_with_pass_fail()
    {
        var turns = LoopTranscriptClassifier.Classify(Transcript);

        Assert.Equal(5, turns.Count);
        Assert.Equal(TurnKind.Read, turns[0].Kind);
        Assert.Equal(TurnKind.Edit, turns[1].Kind);
        Assert.Equal(TurnKind.Build, turns[2].Kind);
        Assert.False(turns[2].Passed); // CS1061 in the result is a failed build.
        Assert.Equal(TurnKind.Edit, turns[3].Kind);
        Assert.Equal(TurnKind.Build, turns[4].Kind);
        Assert.True(turns[4].Passed);
    }

    [Fact]
    public void LoopMetrics_over_the_classified_turns_reports_two_builds_and_green_on_the_second()
    {
        var loop = LoopMetrics.Compute(LoopTranscriptClassifier.Classify(Transcript));

        Assert.True(loop.ReachedGreen);
        Assert.Equal(2, loop.BuildInvocations);
        Assert.Equal(2, loop.IterationsToGreen); // green reached on the second build-gated turn.
    }

    [Fact]
    public void A_fuse_check_turn_is_a_check_not_a_build_but_still_reaches_green()
    {
        const string t = """
            {"type":"assistant","message":{"content":[{"type":"tool_use","id":"c1","name":"mcp__fuse__fuse_check","input":{"file":"Order.cs"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"c1","content":"clean: no errors in the changed document Order.cs"}]}}
            """;

        var turns = LoopTranscriptClassifier.Classify(t);
        var loop = LoopMetrics.Compute(turns);

        Assert.Single(turns);
        // D22a: fuse_check is its own kind, counted apart from agent-visible dotnet build.
        Assert.Equal(TurnKind.Check, turns[0].Kind);
        Assert.True(turns[0].Passed); // "clean" is a passing speculative typecheck.
        Assert.Equal(0, loop.BuildInvocations); // NOT folded into the build column
        Assert.Equal(1, loop.CheckInvocations);
        Assert.Equal(0, loop.AgentVisibleVerifications); // a speculative check is not an agent-visible round-trip
        Assert.True(loop.ReachedGreen); // it still counts toward the reached-green proxy
    }

    [Fact]
    public void A_fuse_check_that_reports_diagnostics_is_a_failed_build()
    {
        const string t = """
            {"type":"assistant","message":{"content":[{"type":"tool_use","id":"c1","name":"mcp__fuse__fuse_check","input":{"file":"Order.cs"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"c1","content":"diagnostics for Order.cs (speculative typecheck): 1"}]}}
            """;

        var turns = LoopTranscriptClassifier.Classify(t);
        Assert.Equal(TurnKind.Check, turns[0].Kind);
        Assert.False(turns[0].Passed);
        Assert.False(LoopMetrics.Compute(turns).ReachedGreen);
    }

    [Fact]
    public void Build_and_check_and_test_are_counted_in_separate_columns()
    {
        // A mixed session: a failing dotnet build, a passing fuse_check, then a passing dotnet test. The three
        // land in their own columns so the loop-collapse metric is not confounded (D22a).
        const string t = """
            {"type":"assistant","message":{"content":[{"type":"tool_use","id":"b1","name":"Bash","input":{"command":"dotnet build"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"b1","content":"error CS1002"}]}}
            {"type":"assistant","message":{"content":[{"type":"tool_use","id":"c1","name":"mcp__fuse__fuse_check","input":{"file":"Order.cs"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"c1","content":"clean: no errors"}]}}
            {"type":"assistant","message":{"content":[{"type":"tool_use","id":"x1","name":"Bash","input":{"command":"dotnet test --filter T"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"x1","content":"Passed!  - Failed:     0, Passed:     3"}]}}
            """;

        var loop = LoopMetrics.Compute(LoopTranscriptClassifier.Classify(t));

        Assert.Equal(1, loop.BuildInvocations);
        Assert.Equal(1, loop.CheckInvocations);
        Assert.Equal(1, loop.TestInvocations);
        Assert.Equal(2, loop.AgentVisibleVerifications); // build + test, not the check
        Assert.True(loop.ReachedGreen); // the fuse_check (2nd verification turn) is the first pass
        Assert.Equal(2, loop.IterationsToGreen);
    }
}
