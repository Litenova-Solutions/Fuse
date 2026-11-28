using System.Runtime.CompilerServices;
using Fuse.Cli;
using Fuse.Engine.Server;
using Fuse.Protocol.Transport;
namespace Fuse;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var command = args.Length > 0 ? args[0] : "help";
        if (command == "engine")
            return await RunEngineAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
        return await CommandDispatcher.DispatchAsync(args).ConfigureAwait(false);
    }

    /// <summary>Runs <c>fuse engine</c> for the repository at <paramref name="rest"/>, or prints its usage.</summary>
    private static async Task<int> RunEngineAsync(string[] rest)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        if (CommandDispatcher.HasHelp(rest))
            return CommandDispatcher.PrintCommandUsage(CommandDispatcher.HelpFor("engine"), 0);
        try
        {
            return rest.Length == 1
                ? await EngineAsync(rest[0]).ConfigureAwait(false)
                : CommandDispatcher.PrintCommandUsage(CommandUsage.Engine, 2);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return 130;
        }
    }

    /// <summary>
    ///     <c>fuse engine &lt;root&gt;</c>. The native client cannot load MSBuild or analyzers, so the engine runs only from
    ///     <see cref="EngineVersion.ManagedAssembly"/>. The check reads <see cref="RuntimeFeature.IsDynamicCodeSupported"/>
    ///     itself, not <see cref="EngineVersion.IsNativeClient"/>, because the native compiler treats that property as a
    ///     constant only where it is read directly, and then leaves the engine, Roslyn and MSBuild out of the native client.
    /// </summary>
    private static async Task<int> EngineAsync(string root)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            await Console.Error.WriteLineAsync($"fuse: the engine runs from {EngineVersion.ManagedAssembly} on the .NET runtime, not from the native client; the client starts it").ConfigureAwait(false);
            return 2;
        }

        return await EngineServer.RunAsync(root).ConfigureAwait(false);
    }
}
