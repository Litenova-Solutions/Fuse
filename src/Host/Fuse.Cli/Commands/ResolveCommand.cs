using System.Text;
using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Retrieval;

namespace Fuse.Cli.Commands;

/// <summary>
///     Deterministically resolves .NET wiring from the index: the implementation for a service, the handler
///     for a request, the action for a route, the options for a config section, or a symbol by name. Reads the
///     persistent index; run <c>index</c> first.
/// </summary>
[CliCommand(
    Name = "resolve",
    Description = "Resolve a service, request, route, config section, or symbol to its semantic target(s).",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class ResolveCommand
{
    private readonly IConsoleUI _consoleUI;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ResolveCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the console UI is null, so this instance must not run.</remarks>
    public ResolveCommand() : this(null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="ResolveCommand" /> class.
    /// </summary>
    /// <param name="consoleUI">The console UI for output.</param>
    public ResolveCommand(IConsoleUI consoleUI) => _consoleUI = consoleUI;

    /// <summary>The workspace directory. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>Resolve a service interface or type to its registered implementation.</summary>
    [CliOption(Required = false, Description = "Resolve a service to its implementation.")]
    public string? Service { get; set; }

    /// <summary>Resolve a request or command to its handler.</summary>
    [CliOption(Required = false, Description = "Resolve a request/command to its handler.")]
    public string? Request { get; set; }

    /// <summary>Resolve a route ("METHOD /pattern") to its action method.</summary>
    [CliOption(Required = false, Description = "Resolve a route (\"POST /api/orders/{id}\") to its handler.")]
    public string? Route { get; set; }

    /// <summary>Resolve a configuration section to its options type.</summary>
    [CliOption(Required = false, Description = "Resolve a config section to its options type.")]
    public string? Config { get; set; }

    /// <summary>Resolve a symbol name to its declaration(s).</summary>
    [CliOption(Required = false, Description = "Resolve a symbol name to its declaration.")]
    public string? Symbol { get; set; }

    /// <summary>
    ///     Runs the resolve command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the resolution has been written.</returns>
    public async Task RunAsync(CliContext context)
    {
        var root = System.IO.Path.GetFullPath(Path);
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(databasePath))
        {
            _consoleUI.WriteError($"No index found at {databasePath}. Run 'fuse index' first.");
            return;
        }

        await using var store = new WorkspaceIndexStore(databasePath);
        await store.InitializeAsync(context.CancellationToken);
        var resolver = new SemanticResolver(store);

        var result = await ResolveAsync(resolver, context.CancellationToken);
        if (result is null)
        {
            _consoleUI.WriteError("Specify one of --service, --request, --route, --config, or --symbol.");
            return;
        }

        _consoleUI.WriteResult(Format(result));
    }

    private Task<ResolveResult>? ResolveAsync(SemanticResolver resolver, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(Service))
            return resolver.ResolveServiceAsync(Service, cancellationToken);
        if (!string.IsNullOrWhiteSpace(Request))
            return resolver.ResolveRequestAsync(Request, cancellationToken);
        if (!string.IsNullOrWhiteSpace(Route))
            return resolver.ResolveRouteAsync(Route, cancellationToken);
        if (!string.IsNullOrWhiteSpace(Config))
            return resolver.ResolveConfigAsync(Config, cancellationToken);
        if (!string.IsNullOrWhiteSpace(Symbol))
            return resolver.ResolveSymbolAsync(Symbol, cancellationToken);
        return null;
    }

    private static string Format(ResolveResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"resolve {result.Target.ToString().ToLowerInvariant()}: {result.Query}");
        if (result.Matches.Count == 0)
        {
            builder.AppendLine("  no matches");
            return builder.ToString();
        }

        foreach (var match in result.Matches)
        {
            var location = match.FilePath is null ? string.Empty : $"  ({match.FilePath}:{match.StartLine})";
            builder.AppendLine($"  [{match.Relation}] {match.Kind} {match.DisplayName}{location}");
            if (match.Signature is not null)
                builder.AppendLine($"      {match.Signature}");
        }

        return builder.ToString();
    }
}
