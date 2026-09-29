using Fuse.Check.Model;

namespace Fuse.Protocol;

/// <summary>One introduced error as the client receives it.</summary>
/// <param name="Error">The error itself, the one check model type the wire carries (decision D11 in docs/architecture.md).</param>
/// <param name="Cause">
///     The declaration change to print under the error, or null when it prints with none: an error in a target, an
///     analyzer error, an error under a broad reach, or an error past the cap on causes per answer.
/// </param>
internal sealed record ReportedError(CompilerError Error, ReportedCause? Cause = null);
