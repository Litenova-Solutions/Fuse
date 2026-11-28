using Fuse.Failures;
using Fuse.Identity;
using Fuse.Protocol.Analysis;
namespace Fuse.Operations.Check;

/// <summary>One check of the working tree: what to check, whose edits it answers, and how to show it.</summary>
/// <param name="Files">Absolute paths of the files to check, or null for every change.</param>
/// <param name="Session">
///     The session that wrote <paramref name="Files"/>, or the session the answer is for when <paramref name="Files"/>
///     is null, as its harness identifies it, or null. With one, the answer leaves out the errors other sessions'
///     edits caused without it. Only <paramref name="IsStopping"/> marks it stopped.
/// </param>
/// <param name="WaitForLoad">Wait for the engine to finish loading, or return immediately with <see cref="ErrorCode.Loading"/>.</param>
/// <param name="CompilationWideAnalyzers">
///     False to leave out the analyzers that read the whole compilation, which the summary then names. Ignored when
///     <paramref name="Files"/> is null: a check of every change runs them.
/// </param>
/// <param name="Limit">How many errors to show: the default count, an asked count, or every error.</param>
/// <param name="Json">True for one JSON object per error instead of text.</param>
/// <param name="IncludeTreeStamp">True to stamp the summary and the results file with the tree the check ended on, for <c>fuse check</c>; hook and MCP answers stay unstamped.</param>
/// <param name="IncludeNonCSharp">False to leave the changed files Fuse does not check out of the summary; the post-edit hook sends false.</param>
/// <param name="Brief">True to request and render Roslyn repair packets (<c>--brief</c>).</param>
/// <param name="InteractiveFast">True for post-edit hooks so body-only edits can use fast span-level binding.</param>
/// <param name="IsStopping">
///     True when the stop hook sends the check: it also marks <paramref name="Session"/> stopped, so a stopped
///     subagent's edits count as their parent's from then on. Only the stop hook sends true; <c>fuse check</c>,
///     <c>fuse gate</c> and MCP send false, so a working session is never marked stopped.
/// </param>
/// <param name="OnBusy">
///     Told each time the engine answers Busy and the client waits; the command line passes
///     <see cref="BusyNotice.For"/>, and a hook passes nothing so it stays silent.
/// </param>
internal sealed record CheckInvocation(
    IReadOnlyList<string>? Files,
    SessionId? Session,
    bool WaitForLoad,
    bool CompilationWideAnalyzers,
    ErrorLimit Limit,
    bool Json,
    bool IncludeTreeStamp,
    bool IncludeNonCSharp,
    bool Brief,
    bool InteractiveFast,
    bool IsStopping = false,
    Action<EngineResponse.Busy>? OnBusy = null);
