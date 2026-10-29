using System.Text.RegularExpressions;

namespace Fuse.Dotnet;

/// <summary>
///     The line under a build error for a file another process holds open: which file, which process MSBuild names, and
///     what to do. It names only what MSBuild's own message says; a message that names no holder gets the generic line.
/// </summary>
internal static partial class LockLine
{
    /// <summary>The line under <paramref name="error"/>, without the indent the answer adds, or null when it is no locked file.</summary>
    /// <param name="root">The paths the answer prints relative to.</param>
    public static string? For(BuildDiagnostic error, string root)
    {
        if (!IsCopyError(error) || !IsLockedMessage(error.Message))
            return null;
        var file = LockedFile(error, root);
        var locked = LockedBy().Match(error.Message);
        if (!locked.Success)
            return $"locked: {file} is being used by another process; stop the running application or build that holds it, then run the build again";
        var holder = $"{locked.Groups["name"].Value} (pid {locked.Groups["pid"].Value})";
        if (IsBuildServer(locked.Groups["name"].Value))
            return $"locked: {file} is held by {holder}, a build server; run dotnet build-server shutdown, then run the build again";
        return $"locked: {file} is held by {holder}; stop that process, then run the build again";
    }

    private static bool IsCopyError(BuildDiagnostic error) =>
        error.Code is "MSB3021" or "MSB3026" or "MSB3027";

    private static bool IsLockedMessage(string message) =>
        message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
        || message.Contains("locked by", StringComparison.OrdinalIgnoreCase);

    /// <summary>The locked file as the answer prints it: the copy target MSBuild quotes, or the error's own file.</summary>
    private static string LockedFile(BuildDiagnostic error, string root)
    {
        var copy = CopyTarget().Match(error.Message);
        if (copy.Success)
            return BuildOutputParser.Relative(copy.Groups["file"].Value.Trim(), root);
        var access = AccessedFile().Match(error.Message);
        if (access.Success)
            return BuildOutputParser.Relative(access.Groups["file"].Value.Trim(), root);
        return error.File;
    }

    private static bool IsBuildServer(string name)
    {
        var bare = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        return bare.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || bare.Equals("msbuild", StringComparison.OrdinalIgnoreCase)
            || bare.Equals("vbcscompiler", StringComparison.OrdinalIgnoreCase)
            || bare.Equals("testhost", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"locked by:\s*""(?<name>[^""]+?)\s*\((?<pid>\d+)\)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LockedBy();

    [GeneratedRegex(@"Could not copy "".+?"" to ""(?<file>.+?)""", RegexOptions.CultureInvariant)]
    private static partial Regex CopyTarget();

    [GeneratedRegex(@"The process cannot access the file '(?<file>[^']+)' because it is being used", RegexOptions.CultureInvariant)]
    private static partial Regex AccessedFile();
}
