using System.Text.RegularExpressions;
using Fuse.Check.Model;
using Fuse.Paths;

namespace Fuse.Check;

/// <summary>
///     Gives each error in a candidate the cause printed on the line under it: the declaration change that put the file
///     in the check. Without it an agent reading <c>src/App/Program.cs(12,5): error CS1061</c> has to guess which of its
///     own edits caused it; with it the answer is on the next line. A target gets no cause: the agent just edited it and
///     knows why.
/// </summary>
internal static partial class CauseLines
{
    /// <summary>At most this many causes per answer, so the cause lines of a change that reaches many files do not outnumber the errors.</summary>
    public const int MaxCauses = 10;

    /// <summary>
    ///     Each error with its cause, in the same order. An error gets no cause when it is in a target, when an analyzer
    ///     reported it, when the reach is not <see cref="Reach.Precise"/>, or when the cap is reached; in the last case it
    ///     is marked <see cref="IntroducedError.IsCauseLeftOut"/>.
    /// </summary>
    /// <param name="errors">The errors in the check's answer, in report order.</param>
    /// <param name="reach">The files the declaration changes reached; only a precise reach names a cause for a file.</param>
    /// <param name="root">The repository, which turns an error's repository-relative path into the path the reach is keyed by.</param>
    /// <param name="targets">The targets, which never get a cause.</param>
    public static IReadOnlyList<IntroducedError> Attach(
        IReadOnlyList<CompilerError> errors,
        Reach reach,
        RepoRoot root,
        IReadOnlyCollection<RepoPath> targets)
    {
        if (reach is not Reach.Precise precise)
            return [.. errors.Select(e => new IntroducedError(e))];

        var targetPaths = new HashSet<RepoPath>(targets);
        var result = new List<IntroducedError>(errors.Count);
        var shown = 0;
        foreach (var error in errors)
        {
            var path = root.PathOf(error.Path);
            if (error.FromAnalyzer || targetPaths.Contains(path) || !precise.Causes.TryGetValue(path, out var cause))
            {
                result.Add(new IntroducedError(error));
                continue;
            }

            if (shown >= MaxCauses)
            {
                result.Add(new IntroducedError(error, IsCauseLeftOut: true));
                continue;
            }

            result.Add(new IntroducedError(error, About(error, cause, precise.AllCauses?.GetValueOrDefault(path))));
            shown++;
        }

        return result;
    }

    /// <summary>
    ///     The cause to print under <paramref name="error"/>: the first of <paramref name="all"/> whose declared name the
    ///     error's message quotes, or <paramref name="first"/> when none does. A file two changes reach, such as one that
    ///     calls both a renamed method and a renamed interface member, so prints under each error the change it is about.
    /// </summary>
    internal static Cause About(CompilerError error, Cause first, IReadOnlyList<Cause>? all)
    {
        if (all is null || all.Count < 2)
            return first;
        foreach (var cause in all)
        {
            if (NameOf(cause.Declaration) is { } name && Regex.IsMatch(error.Message, $@"'[^']*\b{Regex.Escape(name)}\b[^']*'", RegexOptions.CultureInvariant))
                return cause;
        }

        return first;
    }

    /// <summary>
    ///     The name a declaration header declares, as compiler messages quote it: the last identifier before its parameter
    ///     list, accessor block, initializer, base list or indexer bracket, with type arguments removed first. Null for a
    ///     header with no identifier there.
    /// </summary>
    internal static string? NameOf(string declaration)
    {
        var text = declaration;
        for (var previous = ""; previous != text;)
        {
            previous = text;
            text = TypeArguments().Replace(text, "");
        }

        var end = text.IndexOfAny(['(', '{', '=', ':', '[', ';']);
        var head = end < 0 ? text : text[..end];
        var identifiers = Identifier().Matches(head);
        return identifiers.Count == 0 ? null : identifiers[^1].Value;
    }

    [GeneratedRegex(@"<[^<>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex TypeArguments();

    [GeneratedRegex(@"@?[\p{L}_][\p{L}\p{Nd}_]*", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
