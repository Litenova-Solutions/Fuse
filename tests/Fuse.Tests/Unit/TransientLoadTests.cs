using Fuse.Engine.Background;
using Fuse.Engine.Scheduling;
using Fuse.Failures;
using Fuse.Tests.Fixtures;
using Fuse.Workspace.Blueprints;
namespace Fuse.Tests.Unit;

/// <summary>
///     A BuildHost failure is about the extraction host, not the project it interrupted: it is never recorded as
///     the project's load failure, the extraction is tried once more, and the next request extracts again.
///     A failure about the project itself stays recorded until the projects reload.
/// </summary>
public class TransientLoadTests
{
    private const string NotResponding = "[BuildHost PID 7184] The BuildHost process is not responding. Process output:";

    [Theory]
    [InlineData("[BuildHost PID 7184] The BuildHost process is not responding. Process output:\ninfo: Loading C:\\r\\Lib\\Lib.csproj", true)]
    [InlineData("The BuildHost process exited with 1. Process output:\ninfo: BuildHost Runtime Version: .NET 11.0.0", true)]
    [InlineData("Build host is not connected.", true)]
    [InlineData("The build host was started but we were unable to connect to it's pipe. The process exited with 1.", true)]
    [InlineData("The server disconnected unexpectedly.", true)]
    [InlineData("Pipe connect timed out after 60000 ms.", true)]
    [InlineData("MSB4019: The imported project C:\\p\\Directory.Build.targets was not found", false)]
    [InlineData("The project file could not be loaded. Could not find project 'C:\\p\\missing.csproj'", false)]
    [InlineData("MSB4018: ...\\TimeoutTests\\TimeoutTests.csproj: The referenced project does not exist", false)]
    [InlineData("error NU1101: Unable to find package X", false)]
    [InlineData("", false)]
    public void Host_failures_read_as_transient(string message, bool expected) =>
        Assert.Equal(expected, BlueprintEvaluator.IsTransient(message));

    [Fact]
    public void A_directory_named_timeout_still_reads_as_transient()
    {
        // Recall wins over fail-fast for host wording: a directory named exactly Timeout matches the bounded
        // marker, so its failure extracts again per request instead of poisoning the project until reload.
        Assert.True(BlueprintEvaluator.IsTransient("MSB3021: Unable to copy file C:\\p\\Timeout\\x.dll: being used by another process"));
    }

    [Theory]
    [InlineData("host")]
    [InlineData("io")]
    [InlineData("timeout")]
    public async Task A_transient_open_failure_heals_on_retry_and_loads(string faultKind)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var engine = await InProcessEngine.StartAsync();
        var projects = engine.Workspace.PrimaryOverlay.Projects;
        var lib = engine.Workspace.Graph.Projects.Single(p => p.Name == "Lib");
        var calls = 0;
        BlueprintEvaluator.TestOpenFault = absolute =>
        {
            if (!string.Equals(absolute, lib.Path.Absolute, StringComparison.Ordinal))
                return null;
            if (Interlocked.Increment(ref calls) > 1)
                return null;
            return faultKind switch
            {
                "io" => new IOException("Unable to read data from the transport connection: An existing connection was forcibly closed."),
                "timeout" => new TimeoutException("The operation timed out."),
                _ => new InvalidOperationException(NotResponding),
            };
        };
        try
        {
            await projects.LoadAsync([lib], cancellationToken);
        }
        finally
        {
            BlueprintEvaluator.TestOpenFault = null;
        }

        Assert.Equal(2, calls);
        Assert.True(projects.IsLoaded(lib));
        Assert.Null(projects.LoadFailureOf(lib));
        Assert.Contains(engine.Log, line => line.Contains("retrying 1 project(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_persistent_transient_failure_is_answered_but_never_recorded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var engine = await InProcessEngine.StartAsync();
        var projects = engine.Workspace.PrimaryOverlay.Projects;
        var lib = engine.Workspace.Graph.Projects.Single(p => p.Name == "Lib");
        var calls = 0;
        BlueprintEvaluator.TestOpenFault = absolute =>
        {
            if (!string.Equals(absolute, lib.Path.Absolute, StringComparison.Ordinal))
                return null;
            Interlocked.Increment(ref calls);
            return new InvalidOperationException(NotResponding);
        };
        try
        {
            var first = await Assert.ThrowsAsync<FuseException>(() => projects.LoadAsync([lib], cancellationToken));
            Assert.Equal(ErrorCode.LoadFailed, first.Code);
            Assert.Contains("BuildHost", first.Message, StringComparison.Ordinal);
            Assert.Null(projects.LoadFailureOf(lib));
            Assert.Equal(2, calls);

            var second = await Assert.ThrowsAsync<FuseException>(() => projects.LoadAsync([lib], cancellationToken));
            Assert.Equal(ErrorCode.LoadFailed, second.Code);
            Assert.Null(projects.LoadFailureOf(lib));
            Assert.Equal(4, calls);
        }
        finally
        {
            BlueprintEvaluator.TestOpenFault = null;
        }

        Assert.Equal(2, engine.Log.Count(line => line.Contains("retrying 1 project(s)", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_deterministic_open_failure_is_recorded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var engine = await InProcessEngine.StartAsync();
        var projects = engine.Workspace.PrimaryOverlay.Projects;
        var lib = engine.Workspace.Graph.Projects.Single(p => p.Name == "Lib");
        var calls = 0;
        BlueprintEvaluator.TestOpenFault = absolute =>
        {
            if (!string.Equals(absolute, lib.Path.Absolute, StringComparison.Ordinal))
                return null;
            Interlocked.Increment(ref calls);
            return new InvalidOperationException("MSB4019: The imported project C:\\p\\Directory.Build.targets was not found");
        };
        try
        {
            var first = await Assert.ThrowsAsync<FuseException>(() => projects.LoadAsync([lib], cancellationToken));
            Assert.Equal(ErrorCode.LoadFailed, first.Code);
            Assert.NotNull(projects.LoadFailureOf(lib));

            await Assert.ThrowsAsync<FuseException>(() => projects.LoadAsync([lib], cancellationToken));
            Assert.Equal(1, calls);
        }
        finally
        {
            BlueprintEvaluator.TestOpenFault = null;
        }

        Assert.Equal(1, engine.Log.Count(line => line.Contains("blueprint evaluated", StringComparison.Ordinal)));
        Assert.DoesNotContain(engine.Log, line => line.Contains("retrying", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_deterministic_failure_stays_recorded_until_reload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var engine = await InProcessEngine.StartAsync();
        var projects = engine.Workspace.PrimaryOverlay.Projects;
        var app = engine.Workspace.Graph.Projects.Single(p => p.Name == "App");
        engine.Repo.Replace("App/App.csproj", "</Project>", "  <ItemGroup><ProjectReference Include=\"..\\Missing\\Missing.csproj\" /></ItemGroup>\n</Project>");
        int Evaluations() => engine.Log.Count(line => line.Contains("blueprint evaluated", StringComparison.Ordinal));

        var first = await Assert.ThrowsAsync<FuseException>(() => projects.LoadAsync([app], cancellationToken));
        Assert.Equal(ErrorCode.LoadFailed, first.Code);
        Assert.Contains("Missing.csproj", first.Message, StringComparison.Ordinal);
        Assert.NotNull(projects.LoadFailureOf(app));
        Assert.Equal(1, Evaluations());

        await Assert.ThrowsAsync<FuseException>(() => projects.LoadAsync([app], cancellationToken));
        Assert.Equal(1, Evaluations());

        await projects.ResetAsync(cancellationToken);
        Assert.Null(projects.LoadFailureOf(app));
        await Assert.ThrowsAsync<FuseException>(() => projects.LoadAsync([app], cancellationToken));
        Assert.Equal(2, Evaluations());
    }

    [Fact]
    public async Task A_later_successful_extraction_forgets_the_recorded_failure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var engine = await InProcessEngine.StartAsync();
        var projects = engine.Workspace.PrimaryOverlay.Projects;
        var app = engine.Workspace.Graph.Projects.Single(p => p.Name == "App");
        var original = engine.Repo.Read("App/App.csproj");
        engine.Repo.Replace("App/App.csproj", "</Project>", "  <ItemGroup><ProjectReference Include=\"..\\Missing\\Missing.csproj\" /></ItemGroup>\n</Project>");

        await Assert.ThrowsAsync<FuseException>(() => projects.LoadAsync([app], cancellationToken));
        Assert.NotNull(projects.LoadFailureOf(app));

        engine.Repo.Write("App/App.csproj", original);
        await projects.EnsureExtractedAsync([app], cancellationToken);
        Assert.Null(projects.LoadFailureOf(app));

        await projects.LoadAsync([app], cancellationToken);
        Assert.True(projects.IsLoaded(app));
    }

    [Fact]
    public async Task The_preloader_defers_a_transient_batch_unmarked()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a + b", "a + b + 0");
        await engine.SyncAsync("Lib/Calc.cs");
        using var gate = new RequestGate();
        using var shutdown = new CancellationTokenSource();
        var preloader = new Preloader(engine.Workspace, gate, line =>
        {
            lock (engine.Log)
                engine.Log.Add(line);
        });
        var calls = 0;
        var root = engine.Repo.Root.Path;
        BlueprintEvaluator.TestOpenFault = absolute =>
        {
            if (!absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return null;
            Interlocked.Increment(ref calls);
            return new InvalidOperationException(NotResponding);
        };
        try
        {
            preloader.Schedule(shutdown.Token);
            await preloader.WaitAsync();
            var firstCalls = calls;
            Assert.True(firstCalls > 0);

            preloader.Schedule(shutdown.Token);
            await preloader.WaitAsync();
            Assert.True(calls > firstCalls);
        }
        finally
        {
            BlueprintEvaluator.TestOpenFault = null;
        }

        Assert.DoesNotContain(engine.Log, line => line.Contains("skipped:", StringComparison.Ordinal));
        Assert.Contains(engine.Log, line => line.Contains("preload deferred:", StringComparison.Ordinal));
        Assert.All(engine.Workspace.Graph.Projects, p => Assert.Null(engine.Workspace.LoadFailureOf(p)));
    }
}
