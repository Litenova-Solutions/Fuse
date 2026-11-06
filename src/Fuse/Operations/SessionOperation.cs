using System.Globalization;
using Fuse.Engine.Client;
using Fuse.Failures;
using Fuse.Paths;
using Fuse.Protocol;

namespace Fuse.Operations;

/// <summary>
///     <c>fuse session begin</c> and <c>fuse session end</c>: registers an external writer a launcher starts as a
///     session for its path patterns, so a judge's stop leaves that writer's half-written errors to it, and ends the
///     registration when the writer stops. It also reads the session a caller names in its environment
///     (<see cref="EnvironmentSession"/>), which is how a worker without hooks names itself to <c>fuse check</c>,
///     <c>fuse test</c> and <c>fuse mcp</c>.
/// </summary>
internal static class SessionOperation
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>The environment variable that names a caller's session, the one identity a caller passes in its environment.</summary>
    internal const string SessionVariable = "FUSE_SESSION";

    private const string Usage = "usage: fuse session begin <name> --paths <pattern> [<pattern>...] [--pid <pid>] | fuse session begin --from <file> | fuse session end <name>";

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

    /// <summary>
    ///     The session <see cref="SessionVariable"/> names in this process's environment, or null when it names none a
    ///     session can be recorded under. An orchestrator sets it per worker, and the worker's own commands then answer
    ///     only its own errors, exactly as a hook that names a session does.
    /// </summary>
    internal static string? EnvironmentSession => SessionFromValue(Environment.GetEnvironmentVariable(SessionVariable));

    /// <summary>
    ///     <paramref name="value"/> as a session, or null when it is blank, longer than a session the engine records, or
    ///     holds a control character: such a value names no session, as the engine itself decides for a session a hook
    ///     names. The limit is the engine's, held here because this layer may not use the check layer that holds it.
    /// </summary>
    internal static string? SessionFromValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl) ? value : null;

    private static async Task<OperationResult> BeginAsync(RepoRoot root, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        // begin --from <file>: one registration per name in the file, read before any of them is sent.
        if (args.Count >= 2 && (args[1] == "--from" || args[1].StartsWith("--from=", StringComparison.Ordinal)))
        {
            var file = args[1] == "--from" ? args.Count == 3 ? args[2] : null : args[1]["--from=".Length..];
            if (file is null || string.IsNullOrWhiteSpace(file) || (args[1] == "--from" ? args.Count != 3 : args.Count != 2))
                return new OperationResult(Outcome.Unanswered, Usage);
            return await BeginFromFileAsync(root, file, cancellationToken).ConfigureAwait(false);
        }

        // begin <name> --paths <pattern> [<pattern>...], with --pid before --paths or after the patterns; everything
        // else after --paths is a pattern, even one starting with -.
        var pathsAt = -1;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--paths")
            {
                pathsAt = i;
                break;
            }
        }

        if (args.Count < 4 || pathsAt < 2 || string.IsNullOrWhiteSpace(args[1]))
            return new OperationResult(Outcome.Unanswered, Usage);
        var name = args[1];
        int? pid = null;
        // The words between the name and --paths, when there are any, are --pid and its process id alone.
        if (pathsAt > 2)
        {
            var (beforePid, beforeRefusal) = PidFrom(args.Skip(2).Take(pathsAt - 2).ToList());
            if (beforeRefusal is not null)
                return new OperationResult(Outcome.Unanswered, beforeRefusal);
            if (beforePid is null)
                return new OperationResult(Outcome.Unanswered, Usage);
            pid = beforePid;
        }

        var specs = args.Skip(pathsAt + 1).ToList();
        // A --pid at the end names the process after the patterns it was given with.
        if (specs.Count >= 2 && specs[^2] == "--pid")
        {
            if (pid is not null)
                return new OperationResult(Outcome.Unanswered, "fuse: --pid takes one process id; name it once");
            var (endPid, endRefusal) = PidValue(specs[^1]);
            if (endRefusal is not null)
                return new OperationResult(Outcome.Unanswered, endRefusal);
            pid = endPid;
            specs = specs[..^2];
        }
        else if (specs.Count >= 1 && specs[^1].StartsWith("--pid=", StringComparison.Ordinal))
        {
            if (pid is not null)
                return new OperationResult(Outcome.Unanswered, "fuse: --pid takes one process id; name it once");
            var (endPid, endRefusal) = PidValue(specs[^1]["--pid=".Length..]);
            if (endRefusal is not null)
                return new OperationResult(Outcome.Unanswered, endRefusal);
            pid = endPid;
            specs = specs[..^1];
        }

        if (specs.Count == 0)
            return new OperationResult(Outcome.Unanswered, Usage);
        if (!IsValidName(name))
            return new OperationResult(Outcome.Unanswered, $"fuse: {ErrorMessages.InvalidSessionName(name)}");
        var (patterns, refusal) = ValidatePatterns(root, specs);
        if (patterns is null)
            return new OperationResult(Outcome.Unanswered, refusal!);
        var response = await EngineClient.SendAsync(root, new EngineRequest.BeginSession(name, [.. patterns]) { Pid = pid }, Timeout, cancellationToken).ConfigureAwait(false);
        if (response is EngineResponse.Acknowledged)
            return new OperationResult(Outcome.Clean, pid is { } tied
                ? $"fuse: session {name} registered for {patterns.Count} path pattern(s); files under them count as that session's until fuse session end {name}, process {tied} exits or 4 hours pass"
                : $"fuse: session {name} registered for {patterns.Count} path pattern(s); files under them count as that session's until fuse session end {name} or 4 hours pass");
        return OperationResult.Unanswered(response, "session registration");
    }

    /// <summary>
    ///     Registers every name a session file lists, in the order the file first names it, after reading and checking
    ///     the whole file: a file that does not read, or a line, name or pattern in it that cannot hold, registers
    ///     nothing.
    /// </summary>
    private static async Task<OperationResult> BeginFromFileAsync(RepoRoot root, string file, CancellationToken cancellationToken)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new OperationResult(Outcome.Unanswered, $"fuse: cannot read the session file \"{file}\"; name a file of name, tab and pattern lines");
        }

        var (registrations, parseRefusal) = ParseSessionFileLines(lines);
        if (parseRefusal is not null)
            return new OperationResult(Outcome.Unanswered, parseRefusal);
        if (registrations.Count > 64)
            return new OperationResult(Outcome.Unanswered, $"fuse: {ErrorMessages.TooManyRegisteredSessions()}");
        var validated = new List<(string Name, List<string> Patterns)>(registrations.Count);
        foreach (var (name, specs) in registrations)
        {
            if (!IsValidName(name))
                return new OperationResult(Outcome.Unanswered, $"fuse: {ErrorMessages.InvalidSessionName(name)}");
            var (patterns, refusal) = ValidatePatterns(root, specs);
            if (patterns is null)
                return new OperationResult(Outcome.Unanswered, refusal!);
            validated.Add((name, patterns));
        }

        foreach (var (name, patterns) in validated)
        {
            var response = await EngineClient.SendAsync(root, new EngineRequest.BeginSession(name, [.. patterns]), Timeout, cancellationToken).ConfigureAwait(false);
            if (response is not EngineResponse.Acknowledged)
                return OperationResult.Unanswered(response, "session registration");
        }

        return new OperationResult(Outcome.Clean, $"fuse: {validated.Count} session(s) registered from {file}; files under their patterns count as those sessions' until fuse session end <name> or 4 hours pass");
    }

    /// <summary>
    ///     The registrations a session file's lines name: each line is a name, a tab, then one pattern, and a name on
    ///     several lines holds every pattern its lines name, in the order the file first names it. A blank line names
    ///     nothing. A line with no tab, an empty name or an empty pattern refuses the file, as does a file that names
    ///     no session at all.
    /// </summary>
    internal static (IReadOnlyList<(string Name, IReadOnlyList<string> Patterns)> Registrations, string? Refusal) ParseSessionFileLines(IEnumerable<string> lines)
    {
        var order = new List<string>();
        var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var number = 0;
        foreach (var line in lines)
        {
            number++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var tab = line.IndexOf('\t');
            var named = tab > 0 ? line[(tab + 1)..].Split('\t') : [];
            if (tab <= 0 || named.Length == 0 || named.Any(string.IsNullOrWhiteSpace))
                return ([], $"fuse: session file line {number} is not a name, a tab and a pattern; write each line as a name, a tab, then a pattern");
            var name = line[..tab];
            if (!byName.TryGetValue(name, out var patterns))
            {
                byName[name] = patterns = [];
                order.Add(name);
            }

            patterns.AddRange(named);
        }

        if (order.Count == 0)
            return ([], "fuse: the session file names no session; write each line as a name, a tab, then a pattern");
        return ([.. order.Select(name => (name, (IReadOnlyList<string>)byName[name]))], null);
    }

    /// <summary>The process id a <c>--pid</c> in <paramref name="words"/> names, which is <c>--pid</c> and its id or one <c>--pid=</c> word, or the refusal for any other words.</summary>
    private static (int? Pid, string? Refusal) PidFrom(List<string> words)
    {
        if (words.Count == 1 && words[0].StartsWith("--pid=", StringComparison.Ordinal))
            return PidValue(words[0]["--pid=".Length..]);
        if (words.Count == 2 && words[0] == "--pid")
            return PidValue(words[1]);
        return (null, null);
    }

    /// <summary>The process id <paramref name="text"/> names, or the refusal when it names none: a process id is a positive number.</summary>
    private static (int? Pid, string? Refusal) PidValue(string text) =>
        int.TryParse(text, CultureInfo.InvariantCulture, out var pid) && pid > 0
            ? (pid, null)
            : (null, $"fuse: --pid takes a process id, not '{text}'; name the process the launcher started");

    /// <summary>
    ///     The patterns <paramref name="specs"/> name, checked as a begin checks them, or the refusal for the first one
    ///     that cannot hold.
    /// </summary>
    private static (List<string>? Patterns, string? Refusal) ValidatePatterns(RepoRoot root, IReadOnlyList<string> specs)
    {
        if (specs.Count > 32)
            return (null, $"fuse: {ErrorMessages.TooManySessionPatterns(specs.Count)}");
        var patterns = new List<string>(specs.Count);
        foreach (var spec in specs)
        {
            var relative = ToRepoRelative(root, spec);
            if (relative is null)
                return (null, $"fuse: {ErrorMessages.SessionPatternOutsideRepository(spec)}");
            if (!PathPattern.Normalize(relative, out var clean))
                return (null, $"fuse: {ErrorMessages.InvalidSessionPattern(spec)}");
            patterns.Add(clean);
        }

        return (patterns, null);
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
