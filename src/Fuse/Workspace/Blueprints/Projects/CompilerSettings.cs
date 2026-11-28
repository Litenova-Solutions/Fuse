namespace Fuse.Workspace.Blueprints.Projects;

/// <summary>
///     How the compiler builds one evaluated project. Every field maps to a Roslyn compilation or parse
///     option, so the load restores the same settings MSBuild evaluated.
/// </summary>
internal sealed record CompilerSettings(
    IReadOnlyList<string> DefineConstants,
    string LanguageVersion,
    string OutputKind,
    string Nullable,
    bool AllowUnsafe,
    IReadOnlyDictionary<string, string> SpecificDiagnosticOptions,
    string GeneralDiagnosticOption,
    int WarningLevel,
    bool CheckOverflow,
    bool Deterministic,
    string Platform,
    string OptimizationLevel,
    string MetadataImportOptions,
    string DocumentationMode,
    IReadOnlyDictionary<string, string>? Features,
    IReadOnlyList<string>? Usings,
    string? ModuleName,
    string? MainTypeName,
    string? ScriptClassName,
    string? CryptoKeyContainer,
    bool? DelaySign,
    string? CryptoPublicKey,
    string? StrongNameProvider);
