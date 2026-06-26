namespace Fuse.Cli.Verification;

/// <summary>Constructs the API surface analyzer backing the verify command.</summary>
public static class ApiSurfaceAnalyzerFactory
{
    /// <summary>Creates the Roslyn-based analyzer used for all builds.</summary>
    public static IApiSurfaceAnalyzer Create() => new RoslynApiSurfaceAnalyzer();

    /// <summary>Identifies the analysis backend in emitted reports.</summary>
    public static string BackendName => "roslyn";
}
