using System.IO.Pipelines;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using Fuse.Cli.Rpc;
using StreamJsonRpc;
using Xunit;

namespace Fuse.Cli.Tests.Host;

// R4: FUSE_HOOK_VERBOSE=1 logs swallowed hook RPC failures to stderr; default stays silent.
// Shares the ConsoleCapture collection with the other stderr-capturing tests: Console.SetError is process-global,
// so parallel captures corrupt each other's save/restore chain (a verbose log lands in the wrong buffer).
[Collection("ConsoleCapture")]
public sealed class FuseHostClientVerboseTests
{
    private static string UniqueRoot() =>
        Path.Combine(Path.GetTempPath(), "fuse-hook-verbose", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("off", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    public void IsEnabled_parses_truthy_values(string? value, bool expected)
    {
        WithEnvironment(FuseHookVerbose.EnvironmentVariable, value, () =>
            Assert.Equal(expected, FuseHookVerbose.IsEnabled()));
    }

    [Fact]
    public async Task TryCheckDeltaAsync_no_host_default_is_silent()
    {
        await WithCapturedStderrAsync(async stderr =>
        {
            var delta = await FuseHostClient.TryCheckDeltaAsync(
                UniqueRoot(), "hook", TimeSpan.FromMilliseconds(100), CancellationToken.None);

            Assert.Null(delta);
            Assert.Equal(string.Empty, stderr.ToString());
        });
    }

    [Fact]
    public async Task TryCheckDeltaAsync_no_host_verbose_logs()
    {
        await WithCapturedStderrAsync(async stderr =>
        {
            await WithEnvironmentAsync(FuseHookVerbose.EnvironmentVariable, "1", async () =>
            {
                var delta = await FuseHostClient.TryCheckDeltaAsync(
                    UniqueRoot(), "hook", TimeSpan.FromMilliseconds(100), CancellationToken.None);

                Assert.Null(delta);
                var output = stderr.ToString();
                Assert.Contains("fuse/check", output);
                Assert.Contains("no_host", output);
            });
        });
    }

    [Fact]
    public async Task TryCheckDeltaAsync_protocol_mismatch_default_is_silent()
    {
        var root = UniqueRoot();
        Directory.CreateDirectory(root);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServeTargetAsync(root, new StaleHandshakeTarget(), ready, cts.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            await WithCapturedStderrAsync(async stderr =>
            {
                var delta = await FuseHostClient.TryCheckDeltaAsync(
                    root, "hook", TimeSpan.FromSeconds(2), cts.Token);

                Assert.Null(delta);
                Assert.Equal(string.Empty, stderr.ToString());
            });
        }
        finally
        {
            await cts.CancelAsync();
            try { await serverTask; } catch (OperationCanceledException) { }
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task TryCheckDeltaAsync_protocol_mismatch_verbose_logs()
    {
        var root = UniqueRoot();
        Directory.CreateDirectory(root);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServeTargetAsync(root, new StaleHandshakeTarget(), ready, cts.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            await WithCapturedStderrAsync(async stderr =>
            {
                await WithEnvironmentAsync(FuseHookVerbose.EnvironmentVariable, "1", async () =>
                {
                    var delta = await FuseHostClient.TryCheckDeltaAsync(
                        root, "hook", TimeSpan.FromSeconds(2), cts.Token);

                    Assert.Null(delta);
                    var output = stderr.ToString();
                    Assert.Contains("fuse/handshake", output);
                    Assert.Contains("protocol_mismatch", output);
                });
            });
        }
        finally
        {
            await cts.CancelAsync();
            try { await serverTask; } catch (OperationCanceledException) { }
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task TryCheckDeltaAsync_rpc_error_default_is_silent()
    {
        var root = UniqueRoot();
        Directory.CreateDirectory(root);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServeTargetAsync(root, new ThrowingCheckTarget(), ready, cts.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            await WithCapturedStderrAsync(async stderr =>
            {
                var delta = await FuseHostClient.TryCheckDeltaAsync(
                    root, "hook", TimeSpan.FromSeconds(2), cts.Token);

                Assert.Null(delta);
                Assert.Equal(string.Empty, stderr.ToString());
            });
        }
        finally
        {
            await cts.CancelAsync();
            try { await serverTask; } catch (OperationCanceledException) { }
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task TryCheckDeltaAsync_rpc_error_verbose_logs()
    {
        var root = UniqueRoot();
        Directory.CreateDirectory(root);
        // The coverage runner can delay the test server's first RPC completion beyond the default 15 seconds.
        // Keep the failure bounded while allowing the test to observe the intended RPC error on that runner.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServeTargetAsync(root, new ThrowingCheckTarget(), ready, cts.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            await WithCapturedStderrAsync(async stderr =>
            {
                await WithEnvironmentAsync(FuseHookVerbose.EnvironmentVariable, "1", async () =>
                {
                    var delta = await FuseHostClient.TryCheckDeltaAsync(
                        root, "hook", TimeSpan.FromSeconds(2), cts.Token);

                    Assert.Null(delta);
                    var output = stderr.ToString();
                    Assert.Contains("fuse/check", output);
                    Assert.Contains("rpc_error", output);
                });
            });
        }
        finally
        {
            await cts.CancelAsync();
            try { await serverTask; } catch (OperationCanceledException) { }
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static void WithEnvironment(string name, string? value, Action action)
    {
        var original = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, original);
        }
    }

    private static async Task WithEnvironmentAsync(string name, string? value, Func<Task> action)
    {
        var original = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, original);
        }
    }

    private static async Task WithCapturedStderrAsync(Func<StringWriter, Task> action)
    {
        var stderr = new StringWriter();
        var originalError = Console.Error;
        try
        {
            Console.SetError(stderr);
            await action(stderr);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    private static async Task ServeTargetAsync(
        string root, object target, TaskCompletionSource ready, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var server = new NamedPipeServerStream(
                HostEndpoint.PipeName(root), PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
            await using (server)
            {
                ready.TrySetResult();
                await server.WaitForConnectionAsync(cancellationToken);
                using var rpc = AttachTarget(PipeReader.Create(server), PipeWriter.Create(server), target);
                await rpc.Completion.WaitAsync(cancellationToken);
            }
            return;
        }

        var socketPath = HostEndpoint.SocketPath(root);
        if (File.Exists(socketPath))
            File.Delete(socketPath);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        ready.TrySetResult();
        try
        {
            var accepted = await listener.AcceptAsync(cancellationToken);
            var stream = new NetworkStream(accepted, ownsSocket: true);
            await using (stream)
            {
                using var rpc = AttachTarget(PipeReader.Create(stream), PipeWriter.Create(stream), target);
                await rpc.Completion.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            try { File.Delete(socketPath); } catch (IOException) { }
        }
    }

    private static JsonRpc AttachTarget(PipeReader reader, PipeWriter writer, object target)
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, FuseHostJsonContext.Default);
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        var handler = new HeaderDelimitedMessageHandler(writer, reader, formatter);
        var rpc = new JsonRpc(handler);
        rpc.AddLocalRpcTarget(target, new JsonRpcTargetOptions { AllowNonPublicInvocation = true });
        rpc.StartListening();
        return rpc;
    }

    private sealed class StaleHandshakeTarget
    {
        [JsonRpcMethod("fuse/handshake")]
        public FuseHostHandshake Handshake() =>
            new("test", FuseHostService.ProtocolVersion - 1, "token");
    }

    private sealed class ThrowingCheckTarget
    {
        [JsonRpcMethod("fuse/handshake")]
        public FuseHostHandshake Handshake() =>
            new("test", FuseHostService.ProtocolVersion, "token");

        [JsonRpcMethod("fuse/check")]
        public Task<CheckDeltaDto> Check(string sessionToken, string root, string session) =>
            throw new InvalidOperationException("boom");
    }
}
