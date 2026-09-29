using System.Text.Json;
using System.Text.Json.Serialization;
using Fuse.Protocol;

namespace Fuse.Tests.Unit;

/// <summary>
///     The request and response lines the pipe carries. A client and an engine of one build read each other's lines, and
///     an engine of another build still reads the build id, so a mismatch ends in a restart rather than a dropped request.
/// </summary>
public partial class ProtocolJsonTests
{
    private static readonly EngineRequest[] EveryRequest =
    [
        new EngineRequest.Ping(),
        new EngineRequest.ShutDown(),
        new EngineRequest.CheckChanges(WaitForLoad: false),
        new EngineRequest.CheckFiles(["Lib/Calc.cs", "App/Program.cs"], WaitForLoad: true),
        new EngineRequest.PlanAffectedTests(),
        new EngineRequest.PlanAllTests(),
    ];

    [Fact]
    public void Every_request_names_its_case_first_and_reads_back_as_it_was_written()
    {
        foreach (var request in EveryRequest.Select(r => r with { BuildId = "5.1.0/abc", RequestId = "12-3" }))
        {
            var line = ProtocolJson.Serialize(request);
            var read = ProtocolJson.ReadRequest(line);

            Assert.StartsWith($"{{\"request\":\"{request.GetType().Name}\",", line, StringComparison.Ordinal);
            Assert.IsType(request.GetType(), read);
            Assert.Equal(("5.1.0/abc", "12-3"), (read!.BuildId, read.RequestId));
        }
    }

    [Fact]
    public void A_check_of_named_files_carries_them_and_whether_to_wait_for_the_load()
    {
        var line = ProtocolJson.Serialize(new EngineRequest.CheckFiles(["Lib/Calc.cs", "App/Program.cs"], WaitForLoad: false));

        var read = Assert.IsType<EngineRequest.CheckFiles>(ProtocolJson.ReadRequest(line));
        Assert.Equal(["Lib/Calc.cs", "App/Program.cs"], read.Files);
        Assert.False(read.WaitForLoad);
        Assert.Contains("\"waitForLoad\":false", line, StringComparison.Ordinal);
    }

    [Fact]
    public void No_request_has_a_kind_property()
    {
        // An engine of an earlier build reads kind into its own enum, and one value it does not know makes it drop the
        // request instead of answering Restart.
        foreach (var request in EveryRequest)
            Assert.DoesNotContain("\"kind\"", ProtocolJson.Serialize(request), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"request":"RebuildEverything","buildId":"9.9.9/0"}""", "9.9.9/0")]
    [InlineData("""{"version":"5.0.0/0","kind":"Check","wait":true}""", null)]
    [InlineData("""{"request":"Ping","buildId":42}""", null)]
    public void The_build_id_is_read_whatever_the_rest_of_the_request_is(string line, string? buildId) =>
        Assert.Equal(buildId, ProtocolJson.ReadBuildId(line));

    [Fact]
    public void A_request_of_another_shape_cannot_be_read_as_a_request_of_this_build()
    {
        // Why the engine reads the build id first: this is how a 5.0.0 client writes a check.
        var line = """{"version":"5.0.0/0","kind":"Check","wait":true}""";
        Assert.ThrowsAny<Exception>(() => ProtocolJson.ReadRequest(line));
    }

    [Fact]
    public void A_line_that_is_not_an_object_has_no_build_id_to_read() =>
        Assert.Throws<JsonException>(() => ProtocolJson.ReadBuildId("[1, 2]"));

    [Fact]
    public void A_restart_from_an_engine_of_any_build_reads_as_restart()
    {
        // This build writes it the way an engine of an earlier build does, so a client reads it from either.
        Assert.Equal("{\"status\":\"Restart\"}", ProtocolJson.Serialize(new EngineResponse.Restart()));
        Assert.IsType<EngineResponse.Restart>(ProtocolJson.ReadResponse("{\"status\":\"Restart\"}"));
    }

    [Fact]
    public void An_acknowledgement_reads_back_as_one() =>
        Assert.IsType<EngineResponse.Acknowledged>(ProtocolJson.ReadResponse(ProtocolJson.Serialize(new EngineResponse.Acknowledged())));

    [Fact]
    public void A_request_without_its_optional_fields_reads_them_as_their_defaults()
    {
        // Every client of this build writes all of them; a line written by hand or by another tool may not.
        var check = Assert.IsType<EngineRequest.CheckFiles>(ProtocolJson.ReadRequest("""{"request":"CheckFiles"}"""));
        var ping = Assert.IsType<EngineRequest.Ping>(ProtocolJson.ReadRequest("""{"request":"Ping","buildId":null,"requestId":null}"""));

        Assert.Equal(("", ""), (check.BuildId, check.RequestId));
        Assert.Empty(check.Files);
        Assert.False(check.WaitForLoad);
        Assert.Equal(("", ""), (ping.BuildId, ping.RequestId));
    }

    [Fact]
    public void An_engine_of_5_0_0_reads_every_request_of_this_build_and_finds_no_build_id()
    {
        // A 5.0.0 engine answers Restart when the request's version is not its own, which is how a client of this build
        // gets an engine of its own build. It can only do that if it reads the line at all.
        foreach (var request in EveryRequest.Select(r => r with { BuildId = "5.1.0/abc", RequestId = "12-3" }))
        {
            var read = JsonSerializer.Deserialize(ProtocolJson.Serialize(request), Engine500Json.Default.Engine500Request);

            Assert.NotNull(read);
            Assert.Null(read.Version);
        }
    }

    /// <summary>The request record of Fuse 5.0.0, copied from its source, as its engine reads a request line.</summary>
    /// <param name="Version">The client's build id, under the name 5.0.0 gave it.</param>
    /// <param name="Kind">What the client asks for.</param>
    /// <param name="Files">The files to check, or null for every change.</param>
    /// <param name="Wait">Whether to wait for the engine to finish loading.</param>
    /// <param name="AllTests">Whether to plan every test.</param>
    internal sealed record Engine500Request(string Version, Engine500Kind Kind, string[]? Files = null, bool Wait = true, bool AllTests = false);

    /// <summary>The request kinds of Fuse 5.0.0.</summary>
    internal enum Engine500Kind
    {
        Ping,
        Check,
        TestPlan,
        Shutdown,
    }

    /// <summary>The serializer options of Fuse 5.0.0's pipe protocol.</summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
    [JsonSerializable(typeof(Engine500Request))]
    internal sealed partial class Engine500Json : JsonSerializerContext;
}
