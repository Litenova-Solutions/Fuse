using Fuse.Engine;

namespace Fuse.Tests.Unit;

/// <summary>
///     The engine writes one phase line per request and the evals read it back. Both sides go through
///     <see cref="PhaseLine"/>, so these cases pin the format they share.
/// </summary>
public class PhaseLineTests
{
    [Fact]
    public void Round_trips_a_line()
    {
        var line = PhaseLine.Format("1234-7", "Check", [("gate", 1.5), ("sync", 12.5), ("total", 400.0)]);
        Assert.Equal("phases id=1234-7 kind=Check gate=1.5 sync=12.5 total=400.0", line);

        Assert.True(PhaseLine.TryParse(line, out var id, out var kind, out var phases));
        Assert.Equal("1234-7", id);
        Assert.Equal("Check", kind);
        Assert.Equal(3, phases.Count);
        Assert.Equal(1.5, phases["gate"]);
        Assert.Equal(12.5, phases["sync"]);
        Assert.Equal(400.0, phases["total"]);
    }

    [Fact]
    public void Keeps_a_phase_the_reader_does_not_know()
    {
        // An engine from a later build may time a phase this reader has never heard of; the known ones still come through.
        var line = PhaseLine.Format("9-1", "TestPlan", [("gate", 0.5), ("mirror", 3.0), ("emit", 7.5)]);
        Assert.True(PhaseLine.TryParse(line, out _, out var kind, out var phases));
        Assert.Equal("TestPlan", kind);
        Assert.Equal(0.5, phases["gate"]);
        Assert.Equal(7.5, phases["emit"]);
    }

    [Fact]
    public void Refuses_a_line_that_is_not_one()
    {
        // A line with no id cannot be matched to a call, so it is not usable; see the next case.
        foreach (var line in new[] { null, "", "check: 3 file(s) checked", "phases kind=Check total=1.0", "phases id= kind=Check total=1.0" })
            Assert.False(PhaseLine.TryParse(line, out _, out _, out _));
    }

    [Fact]
    public void A_line_with_only_an_id_is_still_one()
    {
        // A request that did no timed work still names itself, so the eval can tell "no phases" from "no such request".
        Assert.True(PhaseLine.TryParse("phases id=9-1", out var id, out var kind, out var phases));
        Assert.Equal("9-1", id);
        Assert.Equal("", kind);
        Assert.Empty(phases);
    }

    [Fact]
    public void Refuses_a_line_whose_id_is_missing_but_reads_the_rest()
    {
        // Without an id the line cannot be matched to a call, so it is not usable; the phases are still parsed.
        Assert.False(PhaseLine.TryParse("phases kind=Check total=1.0", out var id, out var kind, out var phases));
        Assert.Equal("", id);
        Assert.Equal("Check", kind);
        Assert.Equal(1.0, phases["total"]);
    }

    [Fact]
    public void A_request_id_names_its_process_and_counts_up()
    {
        var first = EngineClient.NextRequestId();
        var second = EngineClient.NextRequestId();
        Assert.StartsWith(Environment.ProcessId.ToString(), first);
        Assert.NotEqual(first, second);
    }
}
