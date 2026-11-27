namespace Fuse.Operations.Test;

/// <summary>
///     What <c>dotnet test</c> arguments that only say where the tests are and how to build them name, so
///     <c>fuse test</c> can plan the affected tests for them instead of running everything. An argument that
///     chooses tests, or one Fuse does not know, is not a scope argument: the arguments then pass to
///     <c>dotnet test</c> as they are.
/// </summary>
/// <param name="Paths">Absolute paths naming test projects, solutions, or directories holding them.</param>
/// <param name="BuildSwitches">
///     The build switches a run with MSBuild is given, as argument pairs: a configuration, a framework,
///     <c>--no-build</c>.
/// </param>
/// <param name="BuildOnly">
///     True when a configuration or framework was named, so the plan builds every run with MSBuild.
/// </param>
/// <param name="Configuration">The configuration named, or null.</param>
/// <param name="Framework">The framework named, or null.</param>
internal sealed record TestScopeArguments(string[] Paths, string[] BuildSwitches, bool BuildOnly, string? Configuration, string? Framework)
{
    /// <summary>The scope <paramref name="arguments"/> name, or null when any of them is not a scope argument.</summary>
    /// <param name="workingDirectory">The directory relative paths are read from.</param>
    /// <param name="arguments">The caller's <c>dotnet test</c> arguments.</param>
    public static TestScopeArguments? Parse(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var paths = new List<string>();
        var switches = new List<string>();
        string? configuration = null;
        string? framework = null;

        string? Value(ref int i)
        {
            var token = arguments[i];
            if (token.Contains('='))
                return token[(token.IndexOf('=') + 1)..];
            return ++i < arguments.Count ? arguments[i] : null;
        }

        for (var i = 0; i < arguments.Count; i++)
        {
            var token = arguments[i];
            var option = token.Contains('=') ? token[..token.IndexOf('=')] : token;
            switch (option)
            {
                case "--":
                    return null;
                case "--project":
                case "--solution":
                    if (Value(ref i) is not { } named || Absolute(workingDirectory, named) is not { } project)
                        return null;
                    paths.Add(project);
                    break;
                case "-c":
                case "--configuration":
                    if (Value(ref i) is not { } config)
                        return null;
                    configuration = config;
                    switches.Add("-c");
                    switches.Add(config);
                    break;
                case "-f":
                case "--framework":
                    if (Value(ref i) is not { } target)
                        return null;
                    framework = target;
                    switches.Add("-f");
                    switches.Add(target);
                    break;
                case "--no-build":
                    switches.Add("--no-build");
                    break;
                case "--no-restore":
                case "--nologo":
                case "-nologo":
                    // Already how a planned run goes: MSBuild runs pass --no-restore, and the answer is Fuse's own.
                    break;
                case "-v":
                case "--verbosity":
                    if (Value(ref i) is null)
                        return null;
                    break;
                default:
                    if (option is "-tl" or "--tl" || option.StartsWith("-tl:", StringComparison.Ordinal) || option.StartsWith("-tlp:", StringComparison.Ordinal))
                        break;
                    if (token.StartsWith('-') || Absolute(workingDirectory, token) is not { } path)
                        return null;
                    paths.Add(path);
                    break;
            }
        }

        return new TestScopeArguments([.. paths], [.. switches], configuration is not null || framework is not null, configuration, framework);
    }

    /// <summary><paramref name="path"/> as a full path from <paramref name="workingDirectory"/>, or null when it is no path.</summary>
    private static string? Absolute(string workingDirectory, string path)
    {
        try
        {
            return Path.GetFullPath(path, workingDirectory);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The caller's <c>dotnet test</c> arguments with a positional path spelled the way the runner requires:
    ///     Microsoft.Testing.Platform refuses a project, solution or directory given positionally, so on it a first
    ///     argument naming one becomes <c>--project</c> or <c>--solution</c> with the path. VSTest reads a positional
    ///     path and refuses <c>--project</c>, so on VSTest the path stays positional. An assembly stays positional on
    ///     both, and arguments that already name a project or solution pass unchanged.
    /// </summary>
    internal static string[] NormalizeArguments(string workingDirectory, IReadOnlyList<string> arguments, bool testingPlatform = true)
    {
        string[] normalized = [.. arguments];
        for (var i = 0; i < normalized.Length; i++)
        {
            if (normalized[i] == "--results-directory" && i + 1 < normalized.Length)
                normalized[i + 1] = NormalizeResultsDirectory(workingDirectory, normalized[i + 1]);
            else if (normalized[i].StartsWith("--results-directory=", StringComparison.OrdinalIgnoreCase))
                normalized[i] = "--results-directory=" + NormalizeResultsDirectory(workingDirectory, normalized[i]["--results-directory=".Length..]);
        }

        arguments = normalized;
        if (!testingPlatform
            || arguments.Count == 0
            || arguments.Any(a => a.StartsWith("--project", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--solution", StringComparison.OrdinalIgnoreCase)))
            return [.. arguments];
        var first = arguments[0];
        if (first.StartsWith('-')
            || first.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || first.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return [.. arguments];
        var option = first.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || first.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            ? "--solution"
            : first.EndsWith("proj", StringComparison.OrdinalIgnoreCase)
                ? "--project"
                : IsDirectory(workingDirectory, first) ? "--project" : null;
        return option is null ? [.. arguments] : [option, first, .. arguments.Skip(1)];
    }

    /// <summary>Whether <paramref name="path"/>, read from <paramref name="workingDirectory"/>, is a directory; a path that cannot be read as one is not.</summary>
    private static bool IsDirectory(string workingDirectory, string path)
    {
        try
        {
            return Directory.Exists(Path.GetFullPath(path, workingDirectory));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    ///     A caller-named results directory as a full path <c>dotnet test</c> can use: relative paths are read from the
    ///     working directory, and on Windows a Git Bash spelling (<c>/c/Users/...</c> or <c>/cygdrive/c/...</c>) becomes its
    ///     Windows path, which the runner would otherwise write nowhere Fuse reads. A path that cannot be resolved
    ///     keeps its given form.
    /// </summary>
    private static string NormalizeResultsDirectory(string workingDirectory, string path)
    {
        var value = OperatingSystem.IsWindows() && FromGitBashPath(path) is { } windows ? windows : path;

        try
        {
            return Path.GetFullPath(value, workingDirectory);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            return value;
        }
    }

    /// <summary>
    ///     The Windows path a Git Bash spelling names: <c>/c/Users/...</c> and <c>/cygdrive/c/Users/...</c> both become
    ///     <c>C:\Users\...</c>. Null for any other form.
    /// </summary>
    private static string? FromGitBashPath(string path)
    {
        var rest = path.StartsWith("/cygdrive/", StringComparison.OrdinalIgnoreCase) ? path[10..]
            : path.Length > 2 && path[0] == '/' ? path[1..] : null;
        return rest is { Length: > 2 } && char.IsLetter(rest[0]) && rest[1] == '/'
            ? $"{char.ToUpperInvariant(rest[0])}:\\" + rest[2..].Replace('/', '\\')
            : null;
    }

    /// <summary>The framework the caller's <c>dotnet test</c> arguments name with <c>-f</c> or <c>--framework</c>, or null.</summary>
    internal static string? FrameworkFromArguments(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Equals("-f", StringComparison.Ordinal) || arguments[i].Equals("--framework", StringComparison.OrdinalIgnoreCase))
                return i + 1 < arguments.Count ? arguments[i + 1] : null;
            if (arguments[i].StartsWith("--framework=", StringComparison.OrdinalIgnoreCase))
                return arguments[i]["--framework=".Length..];
        }

        return null;
    }
}
