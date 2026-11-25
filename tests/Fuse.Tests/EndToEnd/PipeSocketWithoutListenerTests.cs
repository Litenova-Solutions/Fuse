using System.Net.Sockets;
using System.Text.Json;
using Fuse.Engine.Client;
using Fuse.Paths;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

/// <summary>
///     A killed engine leaves its pipe's socket file behind with no listener. The next command starts an engine,
///     which removes the file once it holds the root's mutex, and status reports no engine instead of waiting on one
///     that never answers. The plant binds a socket where the engine's pipe will be and closes it without unlinking.
/// </summary>
public class PipeSocketWithoutListenerTests
{
    [Fact]
    public async Task Check_recovers_when_no_engine_listens_on_the_pipe_socket()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "named pipes are socket files only on Linux and macOS");
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            PlantSocketWithoutListener(repo.Root);
            Assert.True(EngineClient.PipeExists(repo.Root.PipeName));

            var check = await FuseProcess.RunAsync(repo.Path, null, "check");
            Assert.Equal(0, check.ExitCode);
            Assert.True(FuseProcess.EngineRunning(repo.Root));

            var status = await FuseProcess.RunAsync(repo.Path, null, "status");
            Assert.StartsWith($"fuse: engine running for {repo.Root.Path} (pid ", status.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task Status_reports_no_engine_when_no_process_listens_on_the_pipe_socket()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "named pipes are socket files only on Linux and macOS");
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            PlantSocketWithoutListener(repo.Root);

            var status = await FuseProcess.RunAsync(repo.Path, null, "status");
            Assert.Equal(0, status.ExitCode);
            Assert.StartsWith($"fuse: no engine runs for {repo.Root.Path}; the next check starts one", status.Stdout, StringComparison.Ordinal);

            var json = await FuseProcess.RunAsync(repo.Path, null, "status", "--json");
            Assert.Equal(0, json.ExitCode);
            using var document = JsonDocument.Parse(json.Stdout);
            Assert.Equal("stopped", document.RootElement.GetProperty("engine").GetString());
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    /// <summary>
    ///     Binds a socket at the pipe's socket path and closes it without unlinking, so the file stays with no
    ///     listener, as after a killed engine. The path is spelled as the runtime builds it, not read from the
    ///     product, so the plant cannot pass vacuously at a path the product never looks at.
    /// </summary>
    private static void PlantSocketWithoutListener(RepoRoot root)
    {
        var path = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + root.PipeName);
        if (File.Exists(path))
            File.Delete(path);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
    }
}
