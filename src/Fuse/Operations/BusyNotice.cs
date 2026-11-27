using Fuse.Protocol.Analysis;
namespace Fuse.Operations;

/// <summary>
///     The callback interactive commands pass the engine client so a long busy wait says it is waiting: it prints
///     once to standard error after the wait has lasted at least 2 s. Hooks pass nothing and stay silent.
/// </summary>
internal static class BusyNotice
{
    /// <summary>
    ///     The callback a command passes for each Busy answer the client retries. It prints once, after the wait has
    ///     lasted at least 2 s: <c>fuse: waiting for engine (position N, about M s)</c>.
    /// </summary>
    /// <param name="output">Where the wait is printed; standard error when null.</param>
    /// <param name="time">What time it is; a test moves it past the 2 s.</param>
    public static Action<EngineResponse.Busy> For(TextWriter? output = null, TimeProvider? time = null)
    {
        var writer = output ?? Console.Error;
        var clock = time ?? TimeProvider.System;
        var started = clock.GetUtcNow();
        var printed = false;
        return busy =>
        {
            if (printed)
                return;
            if (clock.GetUtcNow() - started < TimeSpan.FromSeconds(2))
                return;
            printed = true;
            writer.WriteLine($"fuse: waiting for engine (position {busy.Position}, about {(busy.EstimatedWaitMs + 999) / 1000} s)");
        };
    }
}
