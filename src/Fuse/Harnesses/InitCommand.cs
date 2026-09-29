using System.Text.Json.Nodes;
using Fuse.Failures;
using Fuse.Paths;
using Fuse.Repo;

namespace Fuse.Harnesses;

/// <summary>
///     <c>fuse init</c>: registers Fuse's hooks with every harness the repository already uses, and the MCP server with
///     VS Code, which runs no hooks. Fuse entries in shared settings files are replaced and other entries kept; <c>.github/hooks/fuse.json</c> and <c>.opencode/plugins/fuse.js</c> belong to Fuse and are written whole.
/// </summary>
internal static class InitCommand
{
    public static int Run() => Run(Environment.CurrentDirectory, Console.Out, Console.Error);

    /// <summary>Registers hooks for the repository containing <paramref name="startDirectory"/>.</summary>
    internal static int Run(string startDirectory, TextWriter output, TextWriter error)
    {
        var root = RepoRoot.Find(startDirectory);
        if (root is null)
        {
            error.WriteLine($"fuse: {ErrorMessages.NotARepository}");
            return 2;
        }

        if (!RepoProbe.HasCSharpProjectsAsync(root, CancellationToken.None).GetAwaiter().GetResult())
        {
            error.WriteLine($"fuse: {ErrorMessages.NoProjects}");
            return 2;
        }

        var harnesses = SupportedHarnesses.All.Where(h => h.IsUsedIn(root)).ToList();
        var vsCode = Path.Exists(Path.Combine(root.Path, ".vscode"));
        // A repository that shows no sign of any harness, nor of VS Code, gets Claude Code's hooks.
        if (harnesses.Count == 0 && !vsCode)
            harnesses.Add(new ClaudeCode());

        // Each file is named as it is written, so a failure part way leaves a list of what was written before it.
        var registrations = harnesses.Select<Harness, Func<string>>(h => () => h.RegisterHooks(root)).ToList();
        if (vsCode)
            registrations.Add(() => RegisterMcpServer(root));
        foreach (var register in registrations)
        {
            try
            {
                output.WriteLine($"wrote {register()}");
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or IOException)
            {
                // SettingsFile names the file and what to do in the message.
                error.WriteLine($"fuse: {e.Message}");
                return 2;
            }
        }

        output.WriteLine(harnesses.Count > 0
            ? "fuse: hooks registered; after each edit your agent gets the compiler errors the edit introduced, `dotnet test` runs the affected tests, and `dotnet build` prints only its errors"
            : "fuse: MCP server registered in .vscode/mcp.json; VS Code's agent can call fuse_check, fuse_test and fuse_build");
        return 0;
    }

    /// <summary>Registers <c>fuse mcp</c> in <c>.vscode/mcp.json</c>, keeping the other servers, and returns that path.</summary>
    private static string RegisterMcpServer(RepoRoot root)
    {
        var path = Path.Combine(root.Path, ".vscode", "mcp.json");
        var settings = SettingsFile.Read(root, path);
        var servers = SettingsFile.GetOrAddObject(settings, "servers");
        servers["fuse"] = new JsonObject { ["type"] = "stdio", ["command"] = "fuse", ["args"] = new JsonArray("mcp"), ["cwd"] = "${workspaceFolder}" };
        return SettingsFile.Write(root, path, settings);
    }
}
