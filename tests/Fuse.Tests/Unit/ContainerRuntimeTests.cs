using Fuse.Dotnet;

namespace Fuse.Tests.Unit;

/// <summary>Whether a container runtime answers: <c>docker</c> first, then <c>podman</c>, each by exit code, never throwing.</summary>
public class ContainerRuntimeTests
{
    [Fact]
    public void Docker_answering_is_a_runtime_without_probing_podman()
    {
        var probed = new List<string>();
        int Run(string runtime, string[] arguments)
        {
            probed.Add(runtime);
            Assert.Equal(["info"], arguments);
            return 0;
        }

        Assert.True(ContainerRuntime.IsRunning(Run));
        Assert.Equal(["docker"], probed);
    }

    [Fact]
    public void Docker_failing_falls_through_to_podman()
    {
        var probed = new List<string>();
        int Run(string runtime, string[] _)
        {
            probed.Add(runtime);
            return runtime == "podman" ? 0 : 1;
        }

        Assert.True(ContainerRuntime.IsRunning(Run));
        Assert.Equal(["docker", "podman"], probed);
    }

    [Fact]
    public void No_runtime_answering_is_no_runtime() =>
        Assert.False(ContainerRuntime.IsRunning((_, _) => 1));

    [Fact]
    public void A_missing_binary_is_tried_next_not_thrown()
    {
        var probed = new List<string>();
        int Run(string runtime, string[] _)
        {
            probed.Add(runtime);
            throw new FileNotFoundException($"no {runtime}");
        }

        Assert.False(ContainerRuntime.IsRunning(Run));
        Assert.Equal(["docker", "podman"], probed);
    }

    [Fact]
    public void A_hung_daemon_is_tried_next_not_thrown() =>
        Assert.False(ContainerRuntime.IsRunning((_, _) => throw new TimeoutException("docker info hung")));

    [Fact]
    public void Docker_missing_but_podman_answering_is_a_runtime()
    {
        int Run(string runtime, string[] _) => runtime == "podman" ? 0 : throw new System.ComponentModel.Win32Exception("not found");

        Assert.True(ContainerRuntime.IsRunning(Run));
    }
}
