using System.Text.Json;
using Fuse.Failures;
using Fuse.Operations;
using Fuse.Paths;
using Fuse.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Fuse.Mcp;

/// <summary>
///     <c>fuse mcp</c>: a stdio MCP server with three tools for MCP hosts that cannot run hooks or shell commands. Each
///     tool returns the same text the CLI prints; an answer Fuse could not give is marked with <c>isError</c>.
/// </summary>
internal static class McpCommand
{
    private static readonly Tool[] Tools =
    [
        new()
        {
            Name = "fuse_check",
            Title = "Check C# changes",
            Description = "Compiler and analyzer errors the working tree has and HEAD does not: in the named files, or in every changed file when none are named, and in the files of dependent projects that use a changed declaration. Run after editing C# files. Once Fuse has loaded the repository it answers much faster than dotnet build.",
            InputSchema = Schema("""{"type":"object","properties":{"files":{"type":"array","items":{"type":"string"},"description":"Files to check, absolute or relative to the repository root. Omit to check every file that differs from HEAD."}}}"""),
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
            Description = "Runs dotnet build and reports its errors.",
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
                CallToolHandler = async (request, ct) => await CallAsync(request.Params, Environment.CurrentDirectory, ct).ConfigureAwait(false),
            },
        };
        // The transport reads through Console.In, which follows the console input code page rather than UTF-8, so a path
        // with a non-ASCII character would arrive as different characters. Every host writes UTF-8 over stdio.
        Console.InputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        await using var server = McpServer.Create(new StdioServerTransport("fuse"), options);
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Answers one tool call for the repository that contains <paramref name="workingDirectory"/>.</summary>
    /// <remarks>
    ///     A <c>fuse_check</c> file that is empty, not a string or not a path is refused with the <c>InvalidPath</c> message
    ///     and <c>isError</c>, before anything is sent to the engine.
    /// </remarks>
    internal static async Task<CallToolResult> CallAsync(CallToolRequestParams? request, string workingDirectory, CancellationToken cancellationToken)
    {
        var root = RepoRoot.Find(workingDirectory);
        if (root is null)
            return Text($"fuse: {ErrorMessages.NotARepository}", isError: true);
        var arguments = request?.Arguments ?? new Dictionary<string, JsonElement>();
        OperationResult result;
        switch (request?.Name)
        {
            case "fuse_check":
                IReadOnlyList<string>? files = null;
                if (arguments.TryGetValue("files", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    var named = list.EnumerateArray().Select(f => f.ValueKind == JsonValueKind.String ? f.GetString() : null);
                    (files, var refusal) = CheckOperation.ResolveFiles(named, f => root.PathOf(f).Absolute);
                    if (refusal is not null)
                        return Text(refusal.Text, isError: true);
                }

                (result, _) = await CheckOperation.RunAsync(root, files, session: null, waitForLoad: true, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
                break;
            case "fuse_test":
                var all = arguments.TryGetValue("all", out var flag) && flag.ValueKind == JsonValueKind.True;
                result = await TestOperation.RunAsync(root, root.Path, [], all, cancellationToken).ConfigureAwait(false);
                break;
            case "fuse_build":
                var target = arguments.TryGetValue("target", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                result = await BuildOperation.RunAsync(root, root.Path, target is null ? [] : [target], cancellationToken).ConfigureAwait(false);
                break;
            default:
                return Text($"fuse: unknown tool {request?.Name}; the tools are fuse_check, fuse_test and fuse_build", isError: true);
        }

        return Text(result.Text, isError: result.Outcome == Outcome.Unanswered);
    }

    private static CallToolResult Text(string text, bool isError) =>
        new() { Content = [new TextContentBlock { Text = text }], IsError = isError };

    private static JsonElement Schema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
