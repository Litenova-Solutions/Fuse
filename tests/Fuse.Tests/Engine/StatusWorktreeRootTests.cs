using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Fuse.Operations;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
using Fuse.Protocol.Transport;
using Fuse.Tests.Fixtures;
namespace Fuse.Tests.Engine;

/// <summary>
///     <c>fuse status</c> in a secondary worktree names that worktree in its request, so the shared engine answers
///     with the worktree's own changed files, tree and HEAD instead of the primary's. A pipe server stands in for the
///     engine and records the request line.
/// </summary>
public class StatusWorktreeRootTests
{
    [Fact]
    public async Task Status_in_a_secondary_worktree_names_that_worktree()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });
        var worktreeDir = Path.Combine(Path.GetTempPath(), "fuse-wt-status-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunGit(repo.Root.Path, "worktree", "add", "--detach", worktreeDir);
            var worktree = RepoRoot.Find(worktreeDir);
            Assert.NotNull(worktree);
            using var engine = new RecordingEngine(worktree);

            var result = await StatusOperation.RunAsync(worktree, json: false, TestContext.Current.CancellationToken);
            var line = await engine.FirstRequest.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("engine running", result.Text, StringComparison.Ordinal);
            Assert.Equal(worktree.Path, Assert.IsType<EngineRequest.Status>(ProtocolJson.ReadRequest(line)).WorktreeRoot);
        }
        finally
        {
            PruneWorktree(repo.Root.Path, worktreeDir);
        }
    }

    /// <summary>A pipe server under the root's engine pipe name that records its first request line and answers every connection with one status.</summary>
    private sealed class RecordingEngine : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;
        private readonly TaskCompletionSource<string> _firstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingEngine(RepoRoot root)
        {
            var answer = ProtocolJson.Serialize(new EngineResponse.StatusAnswered(
                new EngineStatus(Environment.ProcessId, 800L * 1024 * 1024, 3, new string('b', 40), "9f2c41aa07b3")));
            // The first instance exists before the constructor returns, so the client finds the pipe and starts no engine.
            var first = NewServer(root);
            _serving = ServeAsync(root, first, answer, _firstRequest, _stop.Token);
        }

        /// <summary>The first request line the engine read.</summary>
        public Task<string> FirstRequest => _firstRequest.Task;

        private static NamedPipeServerStream NewServer(RepoRoot root) =>
            new(root.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        private static async Task ServeAsync(RepoRoot root, NamedPipeServerStream pipe, string answer, TaskCompletionSource<string> firstRequest, CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(stop);
                    var next = NewServer(root);
                    var line = await PipeFraming.ReadLineAsync(pipe, stop);
                    if (line is not null)
                        _ = firstRequest.TrySetResult(line);
                    await pipe.WriteAsync(Encoding.UTF8.GetBytes(answer + "\n"), stop);
                    await pipe.FlushAsync(stop);
                    await pipe.DisposeAsync();
                    pipe = next;
                }
                catch (Exception e) when (e is OperationCanceledException or IOException)
                {
                    await pipe.DisposeAsync();
                    if (stop.IsCancellationRequested)
                        return;
                    pipe = NewServer(root);
                }
            }

            await pipe.DisposeAsync();
        }

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                _serving.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }

            _stop.Dispose();
        }
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}{stdout}");
    }

    /// <summary>Drops the worktree's registration and whatever is left of its directory, best effort.</summary>
    private static void PruneWorktree(string repoPath, string worktreeDir)
    {
        try
        {
            if (Directory.Exists(worktreeDir))
                Directory.Delete(worktreeDir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            RunGit(repoPath, "worktree", "prune");
        }
        catch (InvalidOperationException)
        {
        }
    }
}
