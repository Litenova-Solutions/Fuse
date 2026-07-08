using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     MCP prompt definitions for Fuse (U3): the playbooks that teach the verified-edit loop as selectable prompts.
/// </summary>
/// <remarks>
///     Each prompt is a first-class playbook a client can select by name. It expands to a short, ordered plan built
///     around the eight-tool loop (after an edit, <c>fuse_check</c>; before a signature change, <c>fuse_impact</c>;
///     before done, <c>fuse_review</c>), anchored on the one input that scopes the task (a diagnostic id, an issue,
///     a merge base, a symbol, a route prefix). A model that selects the right prompt gets the phase order for free,
///     so the loop is teachable without a separate skill document. The prompts are pure text templates - they read
///     no workspace state and inject no services - so they never fail; the tools they name do the work.
/// </remarks>
[McpServerPromptType]
public sealed class FusePrompts
{
    /// <summary>
    ///     The playbook for fixing a build error: anchor on the diagnostic, check the proposed fix at compiler
    ///     grade, and confirm nothing else broke before finishing.
    /// </summary>
    /// <param name="diagnosticId">The compiler diagnostic id to fix (for example <c>CS1061</c>), optionally with the file.</param>
    /// <returns>The rendered playbook text.</returns>
    [McpServerPrompt(Name = "fix-build-error")]
    [Description("Playbook: fix a build error at compiler grade. Anchor on the diagnostic id.")]
    public static string FixBuildError(
        [Description("The compiler diagnostic id to fix (for example CS1061), optionally with the file.")] string diagnosticId) =>
        $"""
        Task: fix the build error {diagnosticId}.

        Loop (stop at the first step that resolves it):
        1. fuse_find kind=text (or kind=symbol) to locate the exact site the diagnostic names.
        2. fuse_context on that seed to read the reduced source around it.
        3. Propose the single-file edit, then fuse_check with the proposed content - it returns the
           diagnostics the edit would produce at oracle or build grade, and a repair packet on an
           unambiguous API-shape error. Iterate on the edit until fuse_check is clean.
        4. fuse_impact on any symbol whose signature you changed, to see the blast radius.
        5. fuse_review before you finish, to confirm the change is scoped and nothing else regressed.

        Do not hand-apply until fuse_check is green. Fuse verifies; it does not commit for you.
        """;

    /// <summary>
    ///     The playbook for implementing a feature: localize, read scoped context, edit and check each file, and
    ///     review the whole change against the base.
    /// </summary>
    /// <param name="issue">The issue or task description to implement.</param>
    /// <param name="since">An optional git base ref the work branches from (branch, commit, or <c>HEAD~N</c>).</param>
    /// <returns>The rendered playbook text.</returns>
    [McpServerPrompt(Name = "implement-feature")]
    [Description("Playbook: implement a feature. Anchor on the issue, optionally a git base.")]
    public static string ImplementFeature(
        [Description("The issue or task description to implement.")] string issue,
        [Description("Optional git base ref the work branches from (branch, commit, or HEAD~N).")] string since = "") =>
        $"""
        Task: implement - {issue}.
        {(string.IsNullOrWhiteSpace(since) ? "" : $"Base ref: {since}.\n")}
        Loop:
        1. fuse_find kind=task with the issue text to rank the candidate files (no bodies). If it refuses as
           low-signal, name a concrete anchor (a symbol, route, service, or config) and use the matching kind.
        2. fuse_context on the chosen seeds to read the reduced source you will change.
        3. For each file you edit: propose the content, fuse_check it, iterate until clean.
        4. Before changing any public signature: fuse_impact on the symbol, to see callers and implementers.
        5. fuse_test on the symbols you touched, to run just their covering tests.
        6. fuse_review{(string.IsNullOrWhiteSpace(since) ? "" : $" changedSince={since}")} before done, for the diff-first impact and packed context.
        """;

    /// <summary>
    ///     The playbook for reviewing a pull request: start from the diff-first review, drill into blast radius, and
    ///     run covering tests.
    /// </summary>
    /// <param name="mergeBase">The merge base (branch, commit, or <c>HEAD~N</c>) to diff against.</param>
    /// <returns>The rendered playbook text.</returns>
    [McpServerPrompt(Name = "review-pr")]
    [Description("Playbook: review a pull request. Anchor on the merge base.")]
    public static string ReviewPr(
        [Description("The merge base (branch, commit, or HEAD~N) to diff against.")] string mergeBase) =>
        $"""
        Task: review the change since {mergeBase}.

        Loop:
        1. fuse_review changedSince={mergeBase} - the changed files, the public API delta (breaking or additive),
           the semantic blast radius, and the packed context, with a graded claims block. Read the api-delta first.
        2. fuse_impact on any symbol whose signature the diff changes, to check the callers the diff did not touch.
        3. fuse_test on the changed symbols, to run their covering tests at build grade.
        4. For a paste-ready summary: fuse_review handoff=true (it refuses while a check session is red).
        """;

    /// <summary>
    ///     The playbook for renaming a symbol: measure the blast radius, then execute the rename as a verified diff.
    /// </summary>
    /// <param name="symbol">The fully qualified name of the symbol to rename.</param>
    /// <returns>The rendered playbook text.</returns>
    [McpServerPrompt(Name = "rename-symbol")]
    [Description("Playbook: rename a symbol safely. Anchor on the symbol's fully qualified name.")]
    public static string RenameSymbol(
        [Description("The fully qualified name of the symbol to rename.")] string symbol) =>
        $"""
        Task: rename {symbol}.

        Loop:
        1. fuse_impact on {symbol} - the callers, implementers, and referencing types the rename will touch.
        2. fuse_refactor operation=rename - it drives the rename through Roslyn (so an unrelated same-named symbol
           is not renamed), recompiles, and returns the staged diff only when no new diagnostic is introduced.
        3. Review the staged diff. fuse_workspace action=apply write=true to write it (the one explicit tree-write).
        4. fuse_review before done.
        """;

    /// <summary>
    ///     The playbook for adding an endpoint: find the route neighborhood, add and check the handler, and resolve
    ///     the wiring.
    /// </summary>
    /// <param name="routePrefix">The route prefix the new endpoint lives under (for example <c>/api/orders</c>).</param>
    /// <returns>The rendered playbook text.</returns>
    [McpServerPrompt(Name = "add-endpoint")]
    [Description("Playbook: add an HTTP endpoint. Anchor on the route prefix.")]
    public static string AddEndpoint(
        [Description("The route prefix the new endpoint lives under (for example /api/orders).")] string routePrefix) =>
        $"""
        Task: add an endpoint under {routePrefix}.

        Loop:
        1. fuse_find kind=route with an existing route under {routePrefix}, to resolve the action and its neighborhood.
        2. fuse_context on the controller or endpoint group, to read the surrounding pattern (routing, DI, options).
        3. Add the handler, then fuse_check the file until clean.
        4. fuse_find kind=service or kind=request to confirm the new handler's dependencies resolve in the graph.
        5. fuse_test the handler's covering tests, then fuse_review before done.
        """;
}
