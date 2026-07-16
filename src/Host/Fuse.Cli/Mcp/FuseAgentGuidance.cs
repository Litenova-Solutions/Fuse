namespace Fuse.Cli.Mcp;

/// <summary>
///     Canonical instructions for routing agent work through the Fuse MCP tools.
/// </summary>
internal static class FuseAgentGuidance
{
    /// <summary>
    ///     Gets the managed Markdown block written by <c>fuse mcp install --rules</c>.
    /// </summary>
    internal static string RuleBody { get; } = string.Join(
        "\n",
        "## Fuse usage",
        "",
        "Use Fuse MCP tools only when the requested folder resolves to a Git repository, except for `fuse_reduce`. Fuse resolves nested folders to the nearest repository root, and every nested path shares that root's warm index. On `workspace_identity_unresolved`, use native file tools and do not retry Fuse MCP tools for that folder. `fuse_reduce` remains available for known files or raw content outside Git repositories.",
        "",
        "Use Fuse in .NET repositories when a task needs cross-file context, framework wiring, change impact, or compiler-backed checking. Use native file reads and search for a known file, an exact literal in a small scope, and non-.NET semantic work.",
        "",
        "- For a pull request or branch review with a Git base, start with `fuse_review`.",
        "- For a named service, request, route, or config section, use the matching `fuse_find` kind, then use `fuse_context` for source bodies.",
        "- For an open-ended task in a large or unfamiliar repository, use `fuse_find kind=task`. Use `fuse_workspace action=map` only when you need repository orientation.",
        "- For exact symbol identity, paths, or indexed text, use `fuse_find kind=symbol|path|text`. Use `kind=signatures|neighbors` for signatures and relationships.",
        "- Before changing a signature, call `fuse_impact`.",
        "- Before writing a standalone single-file edit, call `fuse_check` with the complete proposed content. It cannot verify a coordinated multi-file overlay.",
        "- Use `fuse_refactor` for its supported solution-wide operations. Review and apply the returned diff with normal editing tools, then run the repository's required gates.",
        "- Use `fuse_test` for focused covering tests. Its selection is a lower bound and does not replace required build, test, format, or lint commands.",
        "- Use `fuse_review` before handoff to inspect scope and impact. Do not treat it as compiler or test proof.",
        "- Respect verification grades and abstentions. A missing or incomplete index warms automatically. While `index_state` reports a build or contention state, use native search and retry. An `upgrade_pending` syntax index remains usable.");

    /// <summary>
    ///     Gets the MCP initialization instructions advertised to every connected client.
    /// </summary>
    internal static string ServerInstructions { get; } = string.Join(
        "\n",
        "Fuse provides local .NET codebase context, typed framework wiring, change impact, focused tests, and compiler-backed checks. Use it for cross-file .NET work. Start a Git-based review with fuse_review; resolve a named service, request, route, or config with fuse_find; use fuse_find kind=task for open-ended work; call fuse_impact before a signature change; and call fuse_check before writing a standalone single-file edit. Fuse verification does not replace the repository's required build, test, format, or lint gates.",
        "",
        RuleBody,
        "",
        "Tool constraints:",
        "- Every tool except `fuse_reduce` requires a Git repository identity. A nested path resolves to the nearest repository root. An unresolved folder is refused before Fuse starts a daemon or writes an index.",
        "- `fuse_check` checks one complete proposed file without writing it. It reports oracle grade, build grade, or an abstention.",
        "- `fuse_refactor` returns a staged solution-wide diff and never writes the working tree.",
        "- `fuse_workspace action=apply` accepts complete content for one file. It does not apply a multi-file patch.",
        "- The default MCP server delegates compiler state and index writes to one shared `fuse host` daemon per repository. Set `FUSE_DAEMON=0` to serve in-process.",
        "- `fuse_reduce` compacts known files or raw content outside the indexed workspace loop.");
}
