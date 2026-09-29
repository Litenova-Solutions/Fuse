using System.Text.Json.Serialization;
using Fuse.Failures;

namespace Fuse.Protocol;

/// <summary>
///     The engine's answer to one <see cref="EngineRequest"/>. The JSON names the case in a <c>status</c> property,
///     written first, which System.Text.Json reads to choose the record to create.
/// </summary>
/// <remarks>
///     <see cref="Restart"/> is written <c>{"status":"Restart"}</c>, as an engine of an earlier build writes it too, so a
///     client reads a Restart from an engine of any build. It has to stay that way: it is how a client learns that it
///     must replace the engine.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "status")]
[JsonDerivedType(typeof(Acknowledged), nameof(Acknowledged))]
[JsonDerivedType(typeof(CheckAnswered), nameof(CheckAnswered))]
[JsonDerivedType(typeof(PlanAnswered), nameof(PlanAnswered))]
[JsonDerivedType(typeof(Unanswered), nameof(Unanswered))]
[JsonDerivedType(typeof(Restart), nameof(Restart))]
internal abstract record EngineResponse
{
    private EngineResponse()
    {
    }

    /// <summary>The answer to <see cref="EngineRequest.Ping"/> and <see cref="EngineRequest.ShutDown"/>.</summary>
    public sealed record Acknowledged : EngineResponse;

    /// <summary>The answer to a check: the errors the changes introduced and what the check covered.</summary>
    public sealed record CheckAnswered(CheckReport Report) : EngineResponse;

    /// <summary>The answer to a plan request: the runs to start and the summary to print.</summary>
    public sealed record PlanAnswered(TestPlan Plan) : EngineResponse;

    /// <summary>Fuse could not answer. The client prints the message and exits with code 2.</summary>
    /// <param name="Code">Why, as a code a surface can act on: a hook reports <see cref="ErrorCode.RestoreNeeded"/> and stays silent on the rest.</param>
    /// <param name="Message">One line that names the command to run next, where there is one.</param>
    public sealed record Unanswered(ErrorCode Code, string Message) : EngineResponse;

    /// <summary>The engine is from another build and is exiting. The client starts an engine of its own build and sends the request again.</summary>
    public sealed record Restart : EngineResponse;
}
