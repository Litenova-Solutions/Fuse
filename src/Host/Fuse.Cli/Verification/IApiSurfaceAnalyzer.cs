namespace Fuse.Cli.Verification;

/// <summary>
///     Extracts the public API surface (public and protected types and methods, plus ASP.NET route
///     templates) from C# source for the <c>fuse verify</c> command.
/// </summary>
/// <remarks>
///     Implemented by <see cref="RoslynApiSurfaceAnalyzer" />, which parses each file's syntax tree without
///     semantic binding or metadata references.
/// </remarks>
public interface IApiSurfaceAnalyzer
{
    /// <summary>
    ///     Collects declared public API symbols from one source file into the supplied sets.
    /// </summary>
    /// <param name="source">The C# source text.</param>
    /// <param name="types">The set to add public and protected type names to.</param>
    /// <param name="methods">The set to add public and protected method names to.</param>
    /// <param name="routes">The set to add route template strings to.</param>
    void Collect(string source, ISet<string> types, ISet<string> methods, ISet<string> routes);
}
