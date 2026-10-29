namespace Fuse.Dotnet;

/// <summary>
///     Whether a container runtime answers. Test projects that reference Testcontainers start their databases from
///     containers; without a runtime those tests fail before they run, so the plan leaves them out and says so.
/// </summary>
internal static class ContainerRuntime
{
    /// <summary>How long one runtime probe waits: a wedged daemon never holds a plan past twice this.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>True when <c>docker info</c> or <c>podman info</c> exits 0. Never throws and never starts a container.</summary>
    public static bool IsRunning() => IsRunning(Run);

    /// <summary>True when one of the runtimes <paramref name="run"/> probes answers with exit code 0, trying <c>docker</c> first.</summary>
    /// <param name="run">Runs a binary with arguments and answers its exit code, or throws when it cannot run.</param>
    internal static bool IsRunning(Func<string, string[], int> run)
    {
        foreach (var runtime in new[] { "docker", "podman" })
        {
            try
            {
                if (run(runtime, ["info"]) == 0)
                    return true;
            }
            catch (Exception)
            {
                // A missing binary, a timeout, or a daemon that answers nothing: try the next runtime.
            }
        }

        return false;
    }

    /// <summary>Runs one runtime's <c>info</c>, bounded by <see cref="ProbeTimeout"/>; -1 when it cannot run or hangs.</summary>
    private static int Run(string runtime, string[] arguments)
    {
        using var waiting = new CancellationTokenSource(ProbeTimeout);
        try
        {
            return ProcessRunner.RunAsync(runtime, arguments, Environment.CurrentDirectory, waiting.Token).GetAwaiter().GetResult().ExitCode;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
    }
}
