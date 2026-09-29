using Fuse.Check.Model;
using Fuse.Paths;

namespace Fuse.Check;

/// <summary>
///     Gives each error in a candidate the cause printed on the line under it: the declaration change that put the file
///     in the check. Without it an agent reading <c>src/App/Program.cs(12,5): error CS1061</c> has to guess which of its
///     own edits caused it; with it the answer is on the next line. A target gets no cause: the agent just edited it and
///     knows why.
/// </summary>
internal static class CauseLines
{
    /// <summary>At most this many causes per answer, so a wide change cannot bury the errors it explains.</summary>
    public const int MaxLines = 10;

    /// <summary>
    ///     Each error with its cause, in the same order, and how many causes the cap left out. An error gets no cause when
    ///     it is in a target, when an analyzer reported it, when the reach is not <see cref="Reach.Precise"/>, or when the
    ///     cap is reached. The cap is counted over the causes that would have been printed, so the count returned is what
    ///     the answer says it left out.
    /// </summary>
    /// <param name="errors">The errors in the check's answer, in report order.</param>
    /// <param name="reach">The files the declaration changes reached; only a precise reach names a cause for a file.</param>
    /// <param name="absolute">Turns an error's repository-relative path into the absolute one the reach is keyed by.</param>
    /// <param name="targets">Absolute paths of the targets, which never get a cause.</param>
    public static (IReadOnlyList<IntroducedError> Errors, int CausesLeftOut) Attach(
        IReadOnlyList<CompilerError> errors,
        Reach reach,
        Func<string, string> absolute,
        IReadOnlyCollection<string> targets)
    {
        if (reach is not Reach.Precise precise)
            return ([.. errors.Select(e => new IntroducedError(e))], 0);

        var targetPaths = new HashSet<string>(targets, PathRules.PathComparer);
        var result = new List<IntroducedError>(errors.Count);
        var shown = 0;
        var leftOut = 0;
        foreach (var error in errors)
        {
            var path = absolute(error.Path);
            if (error.FromAnalyzer || targetPaths.Contains(path) || !precise.Causes.TryGetValue(path, out var cause))
            {
                result.Add(new IntroducedError(error));
                continue;
            }

            if (shown >= MaxLines)
            {
                leftOut++;
                result.Add(new IntroducedError(error));
                continue;
            }

            result.Add(new IntroducedError(error, cause));
            shown++;
        }

        return (result, leftOut);
    }
}
