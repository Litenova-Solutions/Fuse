using System.Text.Json;
using Fuse.Cli;
using Fuse.Engine;
using Fuse.Repo;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Fuse.Mcp;

/// <summary>
///     <c>fuse mcp</c>: a stdio MCP server with three tools for hosts that cannot run hooks or shell commands. Each
///     tool returns the same text the CLI prints; failures inside Fuse are marked with <c>isError</c>.
/// </summary>
internal static class McpCommand
{
    private static readonly Tool[] Tools =
    [
        new()
        {
            Name = "fuse_check",
            Title = "Check C# changes",
            Description = "Compiler and analyzer errors that the working-tree changes introduced since the last commit, in the edited files and in every project that depends on them. Run after editing C# files. Much faster than dotnet build on a warm repository.",
            InputSchema = Schema("""{"type":"object","properties":{"files":{"type":"array","items":{"type":"string"},"description":"Files to scope the check to. Omit to check every change since the last commit."}}}"""),
            Annotations = new ToolAnnotations { ReadOnlyHint = true, IdempotentHint = true, OpenWorldHint = false },
        },
        new()
        {
            Name = "fuse_test",
            Title = "Run affected tests",
            Description = "Runs the tests affected by the working-tree changes and reports only failures. Set all=true to run every test.",
            InputSchema = Schema("""{"type":"object","properties":{"all":{"type":"boolean","description":"Run every test instead of the affected ones."}}}"""),
            Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = false, OpenWorldHint = false },
        },
        new()
        {
            Name = "fuse_build",
            Title = "Build",
            Description = "Runs dotnet build and reports only errors.",
            InputSchema = Schema("""{"type":"object","properties":{"target":{"type":"string","description":"Project or solution to build. Omit to build what dotnet build picks in the repository root."}}}"""),
            Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = false, OpenWorldHint = false },
        },
    ];

    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "fuse", Version = EngineVersion.Product },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = Tools }),
                CallToolHandler = async (request, ct) => await CallAsync(request.Params, ct).ConfigureAwait(false),
            },
        };
        await using var server = McpServer.Create(new StdioServerTransport("fuse"), options);
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static async Task<CallToolResult> CallAsync(CallToolRequestParams? request, CancellationToken cancellationToken)
    {
        var root = RepoRoot.Find(Environment.CurrentDirectory);
        if (root is null)
            return Text("fuse: the MCP server's working directory is not inside a git repository; start it from your repository", isError: true);
        var arguments = request?.Arguments ?? new Dictionary<string, JsonElement>();
        OperationResult result;
        switch (request?.Name)
        {
            case "fuse_check":
                var files = arguments.TryGetValue("files", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(f => root.Absolute(f.GetString() ?? "")).ToList()
                    : null;
                (result, _) = await CheckOperation.RunAsync(root, files, wait: true, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
                break;
            case "fuse_test":
                var all = arguments.TryGetValue("all", out var flag) && flag.ValueKind == JsonValueKind.True;
                result = await TestOperation.RunAsync(root, root.Path, [], all, cancellationToken).ConfigureAwait(false);
                break;
            case "fuse_build":
                var target = arguments.TryGetValue("target", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                result = await BuildOperation.RunAsync(root.Path, root.Path, target is null ? [] : [target], cancellationToken).ConfigureAwait(false);
                break;
            default:
                return Text($"fuse: unknown tool {request?.Name}; the tools are fuse_check, fuse_test and fuse_build", isError: true);
        }

        return Text(result.Text, result.Failed);
    }

    private static CallToolResult Text(string text, bool isError) =>
        new() { Content = [new TextContentBlock { Text = text }], IsError = isError };

    private static JsonElement Schema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
