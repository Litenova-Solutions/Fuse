namespace Fuse.Engine;

/// <summary>
///     The scheduler refused a request at once: the requests ahead of it, at the lane's recent median, will not
///     finish before the caller's wait ends, so queueing it would only spend that wait. It carries the queue
///     position and the estimated wait a <see cref="Fuse.Protocol.EngineResponse.Busy"/> answer names.
/// </summary>
internal sealed class SchedulerBusyException(int position, long estimatedWaitMs) : Exception($"busy: {position} request(s) ahead, about {estimatedWaitMs} ms")
{
    /// <summary>How many requests will start before the refused one, counting the next reader as 1.</summary>
    public int Position { get; } = position;

    /// <summary>How long the refused request would wait before a reader takes it, in milliseconds.</summary>
    public long EstimatedWaitMs { get; } = estimatedWaitMs;
}
