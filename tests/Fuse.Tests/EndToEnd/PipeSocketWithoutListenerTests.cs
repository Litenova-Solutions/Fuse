using System.Diagnostics;
using System.Text.Json;
using Fuse.Tests.Fixtures;
namespace Fuse.Tests.EndToEnd;

/// <summary>
///     A killed engine leaves its pipe's socket file behind with no listener. The next command starts an engine,
///     which removes the file once it holds the root's mutex, and status reports no engine instead of waiting on one
///     that never answers. The plant kills a running engine, as a crash would.
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
            await PlantSocketWithoutListener(repo);
            Assert.True(File.Exists(Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + repo.Root.PipeName)));

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
            await PlantSocketWithoutListener(repo);

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
    /// <summary>
    ///     Leaves the engine's socket file without a listener the way a crash does: starts an engine and kills it, which
    ///     on Linux and macOS sends SIGKILL, so the engine never closes its pipe. A socket .NET binds itself is not a
    ///     plant: disposing it deletes the file.
    /// </summary>
    private static async Task PlantSocketWithoutListener(FixtureRepo repo)
    {
        var started = await FuseProcess.RunAsync(repo.Path, null, "check");
        Assert.Equal(0, started.ExitCode);
        var status = await FuseProcess.RunAsync(repo.Path, null, "status", "--json");
        using var document = JsonDocument.Parse(status.Stdout);
        using var engine = Process.GetProcessById(document.RootElement.GetProperty("pid").GetInt32());
        engine.Kill();
        await engine.WaitForExitAsync(TestContext.Current.CancellationToken);
        var path = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + repo.Root.PipeName);
        Assert.True(File.Exists(path), $"the killed engine's socket file {path} is gone, so there is nothing to recover from");
        Assert.False(FuseProcess.EngineRunning(repo.Root));
    }
}
