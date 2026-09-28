using System.Text.RegularExpressions;

namespace Fuse.Workspace;

/// <summary>
///     Tells a project that could not be evaluated apart from one MSBuild merely logged something about.
///     MSBuildWorkspace hands both to its failure handler, so without this a repository that builds has every project
///     with a restore warning reported as one fuse cannot check.
/// </summary>
internal static partial class LoadFailure
{
    /// <summary>
    ///     True when the message is a NuGet restore warning: either it carries an <c>NU</c> code, or it is a package
    ///     advisory, which NuGet reports with that fixed wording. Neither describes the project's own compilation, and
    ///     <c>dotnet build</c> reports the identical text as a warning while still producing output for the project, so
    ///     recording it as a failure would make fuse decline to answer in a repository that builds. Anything else (a
    ///     missing project reference, an unresolvable SDK) left the load genuinely broken and stays a failure.
    /// </summary>
    public static bool IsWarning(string message) => NuGetCode().IsMatch(message) || Advisory().IsMatch(message);

    [GeneratedRegex(@"\bNU\d{4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex NuGetCode();

    [GeneratedRegex(@"Package '[^']+'[^\r\n]*?\bhas a known (?:low|moderate|high|critical) severity vulnerability", RegexOptions.CultureInvariant)]
    private static partial Regex Advisory();
}
