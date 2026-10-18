namespace Fuse.Operations;

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
}
