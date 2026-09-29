using System.Text.Json;
using System.Text.Json.Nodes;
using Fuse.Paths;

namespace Fuse.Harnesses;

/// <summary>
///     An agent host that runs Fuse's hooks. Each implementation owns everything that differs between harnesses: how
///     <c>fuse init</c> detects it in a repository, where and in what shape its hooks are registered, how
///     <c>fuse hook</c> answers each event in the harness's format, whether that answer approves a rewritten command, and
///     whether the harness runs the post-edit hook in the background.
/// </summary>
/// <remarks>
///     <c>fuse hook</c> does the work that is the same for every harness (rewriting the command, running the check) and
///     calls an answer method only with its result. The answer methods write nothing themselves, so a test can compare
///     every harness's answers without running a hook.
/// </remarks>
internal abstract class Harness
{
    /// <summary>
    ///     The name <c>fuse hook</c> takes on the command line, which <see cref="RegisterHooks"/> writes into every hook
    ///     command. Registrations already written into users' settings contain it, so it never changes.
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    ///     Whether the harness runs the post-edit hook in the background. Such a hook can wait for the engine's first
    ///     load; a hook the harness runs inline answers only once the engine is warm and leaves the rest to the stop hook.
    /// </summary>
    public virtual bool RunsPostEditInBackground => false;

    /// <summary>
    ///     Whether Cursor also runs the hooks in this harness's settings. In a Cursor session <c>fuse hook</c> then leaves
    ///     the event to the hook Fuse registered with Cursor, so the agent is not told twice.
    /// </summary>
    public virtual bool IsAlsoRunByCursor => false;

    /// <summary>
    ///     Whether the harness's answer to a pre-shell event approves the command it is given, so the harness runs it
    ///     without asking the user. <c>fuse hook</c> then rewrites only a command that is one <c>dotnet build</c> or
    ///     <c>dotnet test</c> with plain arguments and gives any other command no answer, so the approval never covers a
    ///     command the agent wrote besides it.
    /// </summary>
    public virtual bool ApprovesRewrittenCommand => false;

    /// <summary>Whether the repository shows that it uses this harness, from the harness's settings directory or instructions file.</summary>
    public abstract bool IsUsedIn(RepoRoot root);

    /// <summary>
    ///     Registers Fuse's hooks in the harness's settings in <paramref name="root"/>, replacing Fuse's earlier handlers
    ///     and keeping every other entry, and returns the repository-relative path of the file it wrote.
    /// </summary>
    public abstract string RegisterHooks(RepoRoot root);

    /// <summary>The answer to a pre-shell event whose shell command Fuse rewrote.</summary>
    /// <param name="toolInput">The tool input the harness sent, for a harness whose answer replaces all of it.</param>
    /// <param name="command">The command the agent runs instead.</param>
    public abstract HookAnswer ReplaceShellCommand(JsonElement? toolInput, string command);

    /// <summary>The answer to a post-edit event whose check found errors introduced or a project to restore.</summary>
    /// <param name="report">The check's output, which the agent reads.</param>
    public abstract HookAnswer ReportAfterEdit(string report);

    /// <summary>The answer to a stop event that lets the agent finish: nothing to fix, or nothing Fuse could check.</summary>
    public abstract HookAnswer AllowStop();

    /// <summary>The answer to a stop event that sends the agent back to fix the errors before it finishes.</summary>
    /// <param name="reason">The errors and the instruction to fix them, which the agent reads.</param>
    public abstract HookAnswer BlockStop(string reason);

    /// <summary>The hook command this harness runs for <paramref name="hookEvent"/>, one of the <see cref="HookEvent"/> names.</summary>
    protected string Command(string hookEvent) => $"fuse hook {Name} {hookEvent}";

    /// <summary>Whether any of <paramref name="relativePaths"/> exists in the repository, as a file or a directory.</summary>
    protected static bool HasAny(RepoRoot root, params string[] relativePaths) =>
        relativePaths.Any(p => Path.Exists(Path.Combine(root.Path, p)));

    /// <summary>
    ///     Replaces Fuse's handler for <paramref name="harnessEvent"/> in the nested format Claude Code, Gemini CLI and
    ///     Codex share, where an event holds matcher groups and each group a list of handlers. Fuse's earlier handlers
    ///     are removed from every group, a group left empty is removed, and <paramref name="handler"/> is added in a group
    ///     of its own.
    /// </summary>
    /// <param name="hooks">The settings' <c>hooks</c> object.</param>
    /// <param name="harnessEvent">The harness's own name for the event, such as <c>PreToolUse</c>.</param>
    /// <param name="matcher">The harness's tool matcher, or null for an event that has none.</param>
    /// <param name="handler">Fuse's handler.</param>
    protected static void SetNestedHook(JsonObject hooks, string harnessEvent, string? matcher, JsonObject handler)
    {
        var groups = hooks[harnessEvent] as JsonArray ?? [];
        hooks[harnessEvent] = groups;
        foreach (var group in groups.OfType<JsonObject>().ToList())
        {
            if (group["hooks"] is JsonArray handlers)
            {
                foreach (var existing in handlers.OfType<JsonObject>().Where(IsFuse).ToList())
                    handlers.Remove(existing);
                if (handlers.Count == 0)
                    groups.Remove(group);
            }
        }

        var entry = new JsonObject { ["hooks"] = new JsonArray(handler) };
        if (matcher is not null)
            entry.Insert(0, "matcher", matcher);
        groups.Add(entry);
    }

    /// <summary>
    ///     Whether a handler in a harness's settings runs <c>fuse hook</c>, so <see cref="RegisterHooks"/> replaces it. This
    ///     includes a handler for an event name <c>fuse hook</c> no longer accepts, so rerunning <c>fuse init</c> removes it.
    /// </summary>
    protected static bool IsFuse(JsonObject handler) =>
        handler["command"]?.GetValueKind() == JsonValueKind.String && handler["command"]!.GetValue<string>().StartsWith("fuse hook", StringComparison.Ordinal);
}
