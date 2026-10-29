using Fuse.Engine.Client;
using Fuse.Failures;
using Fuse.Paths;
using Fuse.Protocol;

namespace Fuse.Operations;

/// <summary>
///     <c>fuse session begin</c> and <c>fuse session end</c>: registers an external writer a launcher starts as a
///     session for its path patterns, so a judge's stop leaves that writer's half-written errors to it, and ends the
///     registration when the writer stops.
/// </summary>
internal static class SessionOperation
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private const string Usage = "usage: fuse session begin <name> --paths <pattern> [<pattern>...] | fuse session end <name>";

    /// <param name="root">The repository whose engine keeps the registration.</param>
    /// <param name="args">The words after <c>session</c>: <c>begin</c> or <c>end</c> and its arguments.</param>
    /// <param name="cancellationToken">Cancels the wait for the answer.</param>
    public static async Task<OperationResult> RunAsync(RepoRoot root, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0)
            return new OperationResult(Outcome.Unanswered, Usage);
        return args[0] switch
        {
            "begin" => await BeginAsync(root, args, cancellationToken).ConfigureAwait(false),
            "end" => await EndAsync(root, args, cancellationToken).ConfigureAwait(false),
            _ => new OperationResult(Outcome.Unanswered, Usage),
        };
    }

    private static async Task<OperationResult> BeginAsync(RepoRoot root, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        // begin <name> --paths <pattern> [<pattern>...]; everything after --paths is a pattern, even one starting with -.
        var pathsAt = -1;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--paths")
            {
                pathsAt = i;
                break;
            }
        }
        if (args.Count < 4 || string.IsNullOrWhiteSpace(args[1]) || pathsAt != 2)
            return new OperationResult(Outcome.Unanswered, Usage);
        var name = args[1];
        var specs = args.Skip(3).ToList();
        if (specs.Count == 0)
            return new OperationResult(Outcome.Unanswered, Usage);
        if (!IsValidName(name))
            return new OperationResult(Outcome.Unanswered, $"fuse: {ErrorMessages.InvalidSessionName(name)}");
        if (specs.Count > 32)
            return new OperationResult(Outcome.Unanswered, $"fuse: {ErrorMessages.TooManySessionPatterns(specs.Count)}");
        var patterns = new List<string>(specs.Count);
        foreach (var spec in specs)
        {
            var relative = ToRepoRelative(root, spec);
            if (relative is null)
                return new OperationResult(Outcome.Unanswered, $"fuse: {ErrorMessages.SessionPatternOutsideRepository(spec)}");
            if (!PathPattern.Normalize(relative, out var clean))
                return new OperationResult(Outcome.Unanswered, $"fuse: {ErrorMessages.InvalidSessionPattern(spec)}");
            patterns.Add(clean);
        }

        var response = await EngineClient.SendAsync(root, new EngineRequest.BeginSession(name, [.. patterns]), Timeout, cancellationToken).ConfigureAwait(false);
        if (response is EngineResponse.Acknowledged)
            return new OperationResult(Outcome.Clean, $"fuse: session {name} registered for {patterns.Count} path pattern(s); files under them count as that session's until fuse session end {name} or 4 hours pass");
        return OperationResult.Unanswered(response, "session registration");
    }

    private static async Task<OperationResult> EndAsync(RepoRoot root, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count != 2 || string.IsNullOrWhiteSpace(args[1]))
            return new OperationResult(Outcome.Unanswered, Usage);
        var name = args[1];
        var response = await EngineClient.SendAsync(root, new EngineRequest.EndSession(name), Timeout, cancellationToken).ConfigureAwait(false);
        if (response is EngineResponse.Acknowledged)
            return new OperationResult(Outcome.Clean, $"fuse: session {name} ended; its files now count as no session's");
        return OperationResult.Unanswered(response, "session registration");
    }

    /// <summary>True when <paramref name="name"/> is a name the engine registers, as <see cref="Check.SessionEdits"/> decides.</summary>
    private static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 64 && name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>
    ///     <paramref name="spec"/> as a repository-relative pattern with forward slashes, or null when an absolute pattern
    ///     names a path outside the root. A relative pattern is already relative.
    /// </summary>
    private static string? ToRepoRelative(RepoRoot root, string spec)
    {
        if (!Path.IsPathRooted(spec))
            return spec.Replace('\\', '/');
        return PathRules.UnderRoot(root.Path, spec);
    }
}
