using Fuse.Failures;
using Fuse.Protocol;

namespace Fuse.Tests.Unit;

/// <summary>
///     A Busy answer carries the queue position and the estimated wait on the wire, words them the same way on
///     every surface, and a status answer carries the memory and the budget a swarm admission script reads.
/// </summary>
public class BusyTests
{
    [Fact]
    public void The_busy_message_names_the_position_and_the_estimate()
    {
        Assert.Equal("the engine is busy: 2 request(s) ahead, about 2 s; try again", ErrorMessages.Busy(2, 1500));
        Assert.Equal("the engine is busy: 1 request(s) ahead, about 1 s; try again", ErrorMessages.Busy(1, 1));
        Assert.Equal("the engine is busy: 3 request(s) ahead; try again", ErrorMessages.Busy(3, 0));
    }

    [Fact]
    public void A_busy_answer_writes_and_reads_back()
    {
        var answered = Assert.IsType<EngineResponse.Busy>(
            ProtocolJson.ReadResponse(ProtocolJson.Serialize(new EngineResponse.Busy(3, 4000))));

        Assert.Equal(3, answered.Position);
        Assert.Equal(4000, answered.EstimatedWaitMs);
        Assert.Equal(ErrorCode.Busy, Enum.Parse<ErrorCode>("Busy"));
    }

    [Fact]
    public void A_busy_answer_from_an_earlier_line_reads_its_fields()
    {
        var answered = Assert.IsType<EngineResponse.Busy>(
            ProtocolJson.ReadResponse("""{"status":"Busy","position":5,"estimatedWaitMs":2500}"""));

        Assert.Equal(5, answered.Position);
        Assert.Equal(2500, answered.EstimatedWaitMs);
    }

    [Fact]
    public void A_status_answer_carries_the_memory_and_the_budget()
    {
        var answered = Assert.IsType<EngineResponse.StatusAnswered>(
            ProtocolJson.ReadResponse(ProtocolJson.Serialize(new EngineResponse.StatusAnswered(new EngineStatus(4212, 900, 2, null, "9f2c41aa07b3")
            {
                MemoryCeilingBytes = MemoryCeiling,
                MemoryLoadBytes = 12345,
            }))));

        Assert.Equal(MemoryCeiling, answered.Engine.MemoryCeilingBytes);
        Assert.Equal(12345, answered.Engine.MemoryLoadBytes);
    }

    private const long MemoryCeiling = 2L * 1024 * 1024 * 1024;
}
