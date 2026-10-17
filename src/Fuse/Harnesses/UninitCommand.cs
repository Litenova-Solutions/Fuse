using System.Text.Json.Nodes;
using Fuse.Failures;
using Fuse.Operations;
using Fuse.Paths;

namespace Fuse.Harnesses;

/// <summary>
///     <c>fuse uninit</c>: removes what <c>fuse init</c> and <c>fuse init --instructions</c> wrote into the repository, for
///     every harness whether or not the repository still uses it, then stops the repository's engine and deletes its state
///     directory. Entries that are not Fuse's are kept, and a file that held only Fuse's is deleted. The fuse tool itself
///     stays installed.
/// </summary>
internal static class UninitCommand
{
    /// <param name="args">The arguments after <c>uninit</c>, of which there are none.</param>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length > 0)
        {
            await Console.Error.WriteLineAsync("usage: fuse uninit").ConfigureAwait(false);
            return 2;
        }

        return await RunAsync(Environment.CurrentDirectory, Console.Out, Console.Error, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes Fuse from the repository containing <paramref name="startDirectory"/>.</summary>
    internal static async Task<int> RunAsync(string startDirectory, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var root = RepoRoot.Find(startDirectory);
        if (root is null)
        {
            await error.WriteLineAsync($"fuse: {ErrorMessages.NotARepository}").ConfigureAwait(false);
            return 2;
        }

        // Each removal is paired with what the user runs after it, when the harness keeps a copy of its own.
        var removals = SupportedHarnesses.All.Select<Harness, (Func<WrittenFile?> Remove, string? Step)>(h => (() => h.RemoveHooks(root), h.RemovalStep)).ToList();
        removals.Add((() => RemoveMcpServer(root), null));
        removals.Add((() => GitIgnore.RemoveFuseFolder(root), null));
        removals.AddRange(InstructionsFile.AllFiles.Select<string, (Func<WrittenFile?>, string?)>(file => (() => InstructionsFile.Remove(root, file), null)));
        var changed = 0;
        var steps = new List<string>();
        foreach (var (remove, step) in removals)
        {
            try
            {
                if (remove() is { } file)
                {
                    await output.WriteLineAsync($"{(file.Deleted ? "deleted" : "wrote")} {file.Path}").ConfigureAwait(false);
                    changed++;
                    if (step is not null)
                        steps.Add(step);
                }
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or IOException)
            {
                await error.WriteLineAsync($"fuse: {e.Message}").ConfigureAwait(false);
                return 2;
            }
        }

        // The engine holds the repository's state directory open, so it stops first.
        var stopped = await StopOperation.RunAsync(root, all: false, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(stopped.Text).ConfigureAwait(false);
        await output.WriteLineAsync(DeleteState(root)).ConfigureAwait(false);

        await output.WriteLineAsync(changed > 0
            ? "fuse: removed Fuse from this repository; `dotnet tool uninstall -g Fuse` removes the tool itself"
            : "fuse: this repository has nothing fuse init wrote").ConfigureAwait(false);
        foreach (var step in steps)
            await output.WriteLineAsync($"fuse: {step}").ConfigureAwait(false);
        return 0;
    }

    /// <summary>Removes the <c>fuse</c> server from <c>.vscode/mcp.json</c>, keeping the other servers, and returns that file.</summary>
    private static WrittenFile? RemoveMcpServer(RepoRoot root)
    {
        var path = Path.Combine(root.Path, ".vscode", "mcp.json");
        if (!File.Exists(path))
            return null;
        var settings = SettingsFile.Read(root, path);
        if (settings["servers"] is not JsonObject servers || !servers.Remove("fuse"))
            return null;
        if (servers.Count == 0)
            settings.Remove("servers");
        return SettingsFile.WriteOrDelete(root, path, settings);
    }

    /// <summary>Deletes the repository's state directory, its logs and copies included, and says what happened.</summary>
    private static string DeleteState(RepoRoot root)
    {
        var state = root.StateDirectory;
        if (!Directory.Exists(state))
            return "fuse: no engine state to delete";
        try
        {
            Directory.Delete(state, recursive: true);
            return $"fuse: deleted the engine's state in {state}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another fuse command may hold a file there, such as the build lock; the state is outside the repository.
            return $"fuse: could not delete the engine's state in {state} ({e.Message}); delete it once no fuse command runs";
        }
    }
}
