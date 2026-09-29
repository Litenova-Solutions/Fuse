namespace Fuse.Check.Model;

/// <summary>An error the working tree has and HEAD does not, matched by file, id and message, with the cause to print under it.</summary>
/// <param name="Error">The error as the compiler or an analyzer reported it.</param>
/// <param name="Cause">
///     The declaration change that made the error's file a candidate, or null when the error prints with no cause: an
///     error in a target, an analyzer error, an error under a reach that is not precise, and an error past the cap on
///     causes per answer. Only rendering reads it, so a null here changes no control flow.
/// </param>
/// <param name="IsCauseLeftOut">
///     True when the error had a cause and the cap on causes per answer left it out. The client counts these among the
///     errors it prints, so the summary's "cause(s) left out" counts only lines the reader could have seen.
/// </param>
internal sealed record IntroducedError(CompilerError Error, Cause? Cause = null, bool IsCauseLeftOut = false);
