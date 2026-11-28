namespace Fuse.Engine.Scheduling;

/// <summary>
///     The scheduler refused a request at once: the requests ahead of it, at the lane's recent median, will not
///     finish before the caller's wait ends, so queueing it would only spend that wait. It carries the queue
///     position and the estimated wait a <see cref="Fuse.Protocol.Analysis.EngineResponse.Busy"/> answer names.
/// </summary>
internal sealed class SchedulerBusyException : Exception
{
    private readonly int _position;
    private readonly long _estimatedWaitMs;

    public SchedulerBusyException(int position, long estimatedWaitMs)
        : base($"busy: {position} request(s) ahead, about {estimatedWaitMs} ms")
    {
        _position = position;
        _estimatedWaitMs = estimatedWaitMs;
    }

    /// <summary>How many requests will start before the refused one, counting the next reader as 1.</summary>
    public int Position => _position;

    /// <summary>How long the refused request would wait before a reader takes it, in milliseconds.</summary>
    public long EstimatedWaitMs => _estimatedWaitMs;
}
