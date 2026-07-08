using System.Text.Json;
using Fuse.Workspace;
using Xunit;

namespace Fuse.Workspace.Tests;

// T1: the micro-host wire contract round-trips through the source-generated JSON context (Fuse and the host
// executable exchange these over stdin/stdout), with camelCase names and null fields omitted.
public sealed class TestHostContractTests
{
    [Fact]
    public void Request_round_trips_through_the_source_gen_context()
    {
        var request = new TestHostRequest("bin/App.dll", ["ref/a.dll", "ref/b.dll"], ["Ns.A.Test1", "Ns.B.Test2"]);

        var json = JsonSerializer.Serialize(request, TestHostJsonContext.Default.TestHostRequest);
        var back = JsonSerializer.Deserialize(json, TestHostJsonContext.Default.TestHostRequest);

        Assert.Contains("\"assemblyPath\":\"bin/App.dll\"", json);
        Assert.NotNull(back);
        Assert.Equal(request.AssemblyPath, back!.AssemblyPath);
        Assert.Equal(request.ReferencePaths, back.ReferencePaths);
        Assert.Equal(request.TestFullyQualifiedNames, back.TestFullyQualifiedNames);
    }

    [Fact]
    public void Response_round_trips_and_omits_null_message_and_error()
    {
        var response = new TestHostResponse(
            [new TestCaseResult("Ns.A.Passes", "passed", null)],
            ["Ns.A.NeedsDb: environmental dependency"],
            Error: null);

        var json = JsonSerializer.Serialize(response, TestHostJsonContext.Default.TestHostResponse);
        var back = JsonSerializer.Deserialize(json, TestHostJsonContext.Default.TestHostResponse);

        Assert.Contains("\"outcome\":\"passed\"", json);
        Assert.DoesNotContain("\"message\"", json); // null omitted
        Assert.DoesNotContain("\"error\"", json); // null omitted
        Assert.Contains("environmental dependency", json);
        Assert.NotNull(back);
        Assert.Equal("Ns.A.Passes", Assert.Single(back!.Results).Name);
        Assert.Equal(response.NotRunnable, back.NotRunnable);
        Assert.Null(back.Error);
    }

    [Fact]
    public void Failed_result_carries_its_message()
    {
        var response = new TestHostResponse(
            [new TestCaseResult("Ns.A.Fails", "failed", "Assert.Equal() Failure")],
            [],
            Error: null);

        var json = JsonSerializer.Serialize(response, TestHostJsonContext.Default.TestHostResponse);

        Assert.Contains("\"message\":\"Assert.Equal() Failure\"", json);
    }
}
