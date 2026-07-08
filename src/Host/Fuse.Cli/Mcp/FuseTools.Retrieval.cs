using System.ComponentModel;
using System.Text;
using Fuse.Collection.FileSystem;
using Fuse.Context;
using Fuse.Indexing;
using Fuse.Reduction;
using Fuse.Retrieval;
using Fuse.Semantics;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     The retrieval MCP tools (localize, resolve, context, review) over the persistent semantic index.
/// </summary>
public sealed partial class FuseTools
{
    /// <summary>
    ///     Localizes a task to ranked candidate files and symbols (no source bodies).
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="changeSource">The change source for resolving a git base ref.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="task">The free-text task or query.</param>
    /// <param name="route">A route to resolve.</param>
    /// <param name="symbol">A symbol to focus on.</param>
    /// <param name="service">A service to resolve.</param>
    /// <param name="request">A request or command to resolve.</param>
    /// <param name="config">A config section to resolve.</param>
    /// <param name="changedSince">A git base ref whose changed files seed candidates.</param>
    /// <param name="maxCandidates">The maximum candidates to return.</param>
    /// <param name="strict">When true, an insufficient request is refused and only a navigation map is returned; off by default (best-effort).</param>
    /// <param name="expand">When true, the selected candidates are enriched with their typed-graph neighbors for discovery; off by default.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>Ranked candidates with reasons and token costs, or a navigation map when the request is not confident.</returns>
    // Folded into fuse_find (kind=task) in U1; kept as an internal helper the find union calls, shimmed by name.
    public static async Task<string> FuseLocalizeAsync(
        SemanticIndexer indexer,
        IChangeSource changeSource,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("The task or query to localize.")] string? task = null,
        [Description("A route to resolve, for example \"POST /api/orders/{id}\".")] string? route = null,
        [Description("A symbol to focus on.")] string? symbol = null,
        [Description("A service to resolve to its implementation.")] string? service = null,
        [Description("A request/command to resolve to its handler.")] string? request = null,
        [Description("A config section to resolve to its options type.")] string? config = null,
        [Description("A git base ref whose changed files seed the candidates.")] string? changedSince = null,
        [Description("Maximum candidates to return.")] int maxCandidates = 50,
        [Description("Strict signal-sufficiency: when an insufficient request has no clear anchor, refuse and return only a navigation map instead of a low-confidence guess. Off by default (best-effort).")] bool strict = false,
        [Description("Expand the selected candidates with their typed-graph neighbors (implementers, callers, config) for discovery. Off by default; widens recall but pressures precision.")] bool expand = false,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(path);
        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var engine = new SemanticRetrievalEngine(store, changeSource);
        var requestModel = new LocalizationRequest(
            root, Query: task, ChangedSince: changedSince, Route: route, Focus: symbol, Service: service,
            Request: request, ConfigSection: config, MaxCandidates: maxCandidates, Strict: strict, ExpandGraph: expand);
        var result = await engine.LocalizeAsync(requestModel, cancellationToken);
        return LocalizationFormatter.Format(result);
    }

    /// <summary>
    ///     Batch exact-signature lookup: for a set of symbol names, returns each declared signature, kind,
    ///     accessibility, and location from the semantic index in one call. The compiler-shaped answer to the
    ///     agent's most common question ("what is the exact shape of this member"), replacing many grep-and-read
    ///     round-trips.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="names">The symbol names to look up (simple or fully qualified).</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="limitPerName">The maximum matches to return per requested name.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The signatures grouped by requested name, with a note for any name that did not match.</returns>
    // Folded into fuse_find (kind=signatures) in U1; kept as an internal helper the find union calls, shimmed by name.
    public static async Task<string> FuseSignaturesAsync(
        SemanticIndexer indexer,
        [Description("Symbol names to look up (simple name or fully qualified).")] string[] names,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("Maximum matches to return per requested name.")] int limitPerName = 5,
        CancellationToken cancellationToken = default)
    {
        if (names is null || names.Length == 0)
            return "Error: provide one or more symbol names in 'names'.";

        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var matches = await store.GetSignaturesByNamesAsync(names, limitPerName, cancellationToken);

        var builder = new StringBuilder();
        builder.AppendLine(await OracleAvailabilityHeaderAsync(store, Path.GetFullPath(path), cancellationToken));
        foreach (var requested in names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.Ordinal))
        {
            var forName = matches
                .Where(m => string.Equals(m.Name, requested, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(m.FullyQualifiedName, requested, StringComparison.OrdinalIgnoreCase))
                .ToList();
            builder.AppendLine($"# {requested}");
            if (forName.Count == 0)
            {
                builder.AppendLine("  no match in the index (check the name, or run fuse_index if the file is new).");
                continue;
            }

            foreach (var m in forName)
            {
                var accessibility = string.IsNullOrEmpty(m.Accessibility) ? "" : m.Accessibility + " ";
                var signature = string.IsNullOrEmpty(m.Signature)
                    ? $"{m.Kind} {m.FullyQualifiedName} (no signature recorded; index is syntax-tier for this file)"
                    : $"{accessibility}{m.Signature}";
                var container = string.IsNullOrEmpty(m.ContainingType) ? "" : $" in {m.ContainingType}";
                builder.AppendLine($"  {signature}{container}");
                builder.AppendLine($"    {m.FilePath}:{m.StartLine} [{m.Kind}{(m.IsPublicApi ? ", public-api" : "")}]");
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    ///     Iterative exploration primitives: the graph neighborhood of a file, the callers and implementers of a
    ///     symbol, or the structurally central files of an area. Ranked, bounded, and body-free, for chaining.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="file">A file whose graph neighborhood to return.</param>
    /// <param name="symbol">A symbol whose callers and implementers to return.</param>
    /// <param name="centralIn">An area (folder prefix, or empty for the whole workspace) whose central files to return.</param>
    /// <param name="limit">The maximum results to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The ranked exploration items with provenance and no bodies.</returns>
    // Folded into fuse_find (kind=neighbors) in U1; kept as an internal helper the find union calls, shimmed by name.
    public static async Task<string> FuseNeighborsAsync(
        SemanticIndexer indexer,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("A file whose graph neighborhood to return.")] string? file = null,
        [Description("A symbol whose callers and implementers to return.")] string? symbol = null,
        [Description("An area (folder prefix, or empty for the whole workspace) whose central files to return.")] string? centralIn = null,
        [Description("Maximum results to return.")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var explorer = new GraphNeighborhoodExplorer(store);

        string mode;
        IReadOnlyList<ExploredItem> items;
        if (!string.IsNullOrWhiteSpace(file))
        {
            mode = $"neighborhood of {file}";
            items = await explorer.NeighborhoodAsync(file, limit, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(symbol))
        {
            mode = $"callers and implementers of {symbol}";
            items = await explorer.CallersAndImplementersAsync(symbol, limit, cancellationToken);
        }
        else if (centralIn is not null)
        {
            mode = centralIn.Length == 0 ? "central files (workspace)" : $"central files in {centralIn}";
            items = await explorer.CentralFilesAsync(centralIn, limit, cancellationToken);
        }
        else
        {
            return "Error: specify one of file, symbol, or centralIn.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"neighbors ({mode}): {items.Count}");
        foreach (var item in items)
        {
            var symbolPart = item.Symbol is null ? string.Empty : $"  {item.Symbol}";
            builder.AppendLine($"  {item.Path}{symbolPart}  [{item.Reason}]");
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Blast radius for a symbol before an edit: the callers, implementers, consumers, and referencing types a
    ///     change to it would touch, enumerated from the persisted semantic graph (R5's reference edges plus the
    ///     wiring edges). The precise signature-change break set requires an oracle-grade load and is reported as
    ///     unavailable otherwise, per the availability contract.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="symbol">The symbol whose blast radius to compute.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="limit">The maximum impacted items to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The impacted files and symbols with the edge that connects them, plus an availability note.</returns>
    [McpServerTool(Name = "fuse_impact", ReadOnly = true)]
    [Description("Blast radius for a symbol before you edit it: the callers, implementers, consumers, and referencing types a change would touch, from the persisted semantic graph. No bodies. The exact signature-change break set (which call sites would no longer bind) needs an oracle-grade (tier-1) load and is reported unavailable otherwise, rather than guessed. Package-upgrade mode (F3): pass package + fromVersion + toVersion to get the public-API break set between two cached NuGet package versions (removed/changed public members), so a bump's risk is knowable before the lockfile changes; it abstains when a version is not in the local cache and names its blind spots.")]
    public static async Task<string> FuseImpactAsync(
        SemanticIndexer indexer,
        [Description("The symbol (simple or qualified name) whose blast radius to compute.")] string symbol = "",
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("Maximum impacted items to return.")] int limit = 50,
        [Description("Package-upgrade mode: the NuGet package id whose bump to analyze.")] string package = "",
        [Description("Package-upgrade mode: the currently referenced version.")] string fromVersion = "",
        [Description("Package-upgrade mode: the target (upgrade) version.")] string toVersion = "",
        CancellationToken cancellationToken = default)
    {
        // Package-upgrade mode (F3): diff two cached package versions' public API. Independent of the workspace
        // index (it reads the NuGet cache), so it runs before the symbol blast-radius path.
        if (!string.IsNullOrWhiteSpace(package))
        {
            if (string.IsNullOrWhiteSpace(fromVersion) || string.IsNullOrWhiteSpace(toVersion))
                return "Error: package-upgrade mode needs package, fromVersion, and toVersion.";
            return RenderPackageUpgrade(Fuse.Retrieval.PackageUpgradeOracle.AnalyzeCachedVersions(package, fromVersion, toVersion));
        }

        if (string.IsNullOrWhiteSpace(symbol))
            return "Error: provide a symbol name (or package + fromVersion + toVersion for a package-upgrade analysis).";

        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var mode = await store.GetMetaAsync("index_mode", cancellationToken) ?? "unknown";
        var explorer = new GraphNeighborhoodExplorer(store);
        var impact = await explorer.CallersAndImplementersAsync(symbol, limit, cancellationToken);

        var builder = new StringBuilder();
        builder.AppendLine(await OracleAvailabilityHeaderAsync(store, Path.GetFullPath(path), cancellationToken));
        builder.AppendLine($"impact of {symbol}: {impact.Count} impacted (index mode {mode})");
        foreach (var item in impact)
        {
            var symbolPart = item.Symbol is null ? string.Empty : $"  {item.Symbol}";
            builder.AppendLine($"  {item.Path}{symbolPart}  [{item.Reason}]");
        }

        if (impact.Count == 0 && mode == "syntax")
            builder.AppendLine("  (no edges: syntax mode has no semantic graph; run fuse_index on a semantically loadable checkout)");

        builder.AppendLine();
        builder.AppendLine(await BuildImpactApiSurfaceLineAsync(store, symbol, mode, cancellationToken));

        // M1 covering-test selection (down-payment): the tests that reach the symbol through R5's DI-resolved
        // tests edges, called out distinctly from the blast radius so an agent can run just this subset. Best-
        // effort and bounded by R5 edge completeness, so it is labeled a lower bound, never "all the tests".
        var covering = await explorer.CoveringTestsAsync(symbol, limit, cancellationToken);
        builder.AppendLine();
        builder.AppendLine($"covering tests: {covering.Count} (a lower bound from R5 tests edges; run with your own --filter)");
        foreach (var t in covering)
            builder.AppendLine($"  {t.Path}  {t.Symbol}");

        // Availability contract: the exact signature-change break set is an oracle-grade answer (a bind-check
        // against a resident compilation). No tier-1 load exists yet, so it is reported unavailable, not guessed.
        builder.AppendLine();
        builder.AppendLine(
            "signature-change break set: unavailable (needs an oracle-grade tier-1 load; the enumeration above is " +
            "the graph-grade blast radius from the persisted reference and wiring edges).");

        // The graded claims block (U2): the impact answer's statements, each graded from the evidence behind it.
        // Both rest on the persisted graph, so they cap at partially_verified (not compiler-confirmed).
        var claims = new List<Claim>
        {
            Claim.FromGraph($"{impact.Count} caller(s)/implementer(s) reach {symbol}", $"graph: {mode} reference and wiring edges"),
            Claim.FromGraph($"{covering.Count} covering test(s) reach {symbol} (a lower bound)", "graph: R5 tests edges"),
        };
        builder.AppendLine();
        builder.AppendLine(ClaimLedger.Render(claims));

        return builder.ToString();
    }

    /// <summary>
    ///     Runs the covering tests for a symbol (T1): the tests that reach it through the persisted <c>tests</c>
    ///     edges, run at build grade (<c>dotnet test</c> scoped by filter to just those test types), with the whole
    ///     suite never run. Selection-only when no <c>tests</c> edge reaches the symbol; the grade is stamped.
    /// </summary>
    /// <param name="indexer">The semantic indexer (opens the store for covering selection).</param>
    /// <param name="symbol">The symbol whose covering tests to run.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="limit">The maximum covering test types to run.</param>
    /// <param name="cancellationToken">A token to cancel the run.</param>
    /// <returns>The per-test verdicts plus the grade, or the selection-only floor when nothing covers the symbol.</returns>
    [McpServerTool(Name = "fuse_test", ReadOnly = true)]
    [Description("Run the covering tests for a symbol: the tests that reach it through the persisted tests edges, run at build grade (dotnet test scoped by filter to just those test types, the whole suite never run), with per-test verdicts. Selection-only when no tests edge reaches the symbol. Build-grade runs the real build; the emit fast path is future work.")]
    public static async Task<string> FuseTestAsync(
        SemanticIndexer indexer,
        [Description("The symbol whose covering tests to run.")] string symbol = "",
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("Maximum covering test types to run.")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return "Error: provide a symbol name whose covering tests to run.";

        var root = Path.GetFullPath(path);
        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var covering = await new GraphNeighborhoodExplorer(store).CoveringTestsAsync(symbol, limit, cancellationToken);
        if (covering.Count == 0)
            return $"covering tests for {symbol}: none (no tests edge reaches it, so there is nothing to run). This is the selection-only floor; a test reached only by reflection has no edge and is not selected.";

        var discovery = await new Fuse.Semantics.DotNetWorkspaceDiscoverer().DiscoverAsync(root, cancellationToken);
        var target = discovery.SolutionPath ?? discovery.ProjectPaths.FirstOrDefault();
        if (target is null)
            return $"covering tests for {symbol}: {covering.Count} test type(s) selected, but no solution or project was found to run them (selection-only).";

        var filter = Fuse.Workspace.TestFilterBuilder.BuildContains(covering.Select(c => c.Symbol));
        var scratch = Path.Combine(Path.GetTempPath(), "fuse-test", Guid.NewGuid().ToString("N"));
        try
        {
            var result = await Fuse.Workspace.BuildGradeTestRunner.RunAsync(
                target, filter, scratch, TimeSpan.FromMinutes(10), cancellationToken);

            var builder = new StringBuilder();
            builder.AppendLine("verification grade: build (ran dotnet test scoped to the covering tests; the emit fast path is future work)");
            if (result.TimedOut)
            {
                builder.AppendLine($"covering tests for {symbol}: timed out and the test host was killed. Narrow the change or raise the budget.");
                return builder.ToString().TrimEnd();
            }

            if (result.Diagnostics is not null)
            {
                builder.AppendLine($"covering tests for {symbol}: {result.Diagnostics}");
                return builder.ToString().TrimEnd();
            }

            var passed = result.Verdicts.Count(v => v.Outcome == "passed");
            var failed = result.Verdicts.Count(v => v.Outcome == "failed");
            var notRun = result.Verdicts.Count(v => v.Outcome == "not-run");
            builder.AppendLine($"covering tests for {symbol}: {covering.Count} test type(s), {result.Verdicts.Count} test(s) run - {passed} passed, {failed} failed, {notRun} not-run");
            foreach (var verdict in result.Verdicts.OrderBy(v => v.Outcome == "failed" ? 0 : 1).ThenBy(v => v.Name, StringComparer.Ordinal))
                builder.AppendLine($"  {verdict.Outcome} {verdict.Name}");

            // Covering types that produced no verdict are reported not-runnable by name, never counted green.
            var notRunnable = Fuse.Workspace.CoveringRunAnalysis.NotRunnableTypes(
                covering.Select(c => c.Symbol).ToList(), result.Verdicts);
            if (notRunnable.Count > 0)
            {
                builder.AppendLine($"not-runnable ({notRunnable.Count}; selected but produced no result - a collection error or no runnable test):");
                foreach (var type in notRunnable)
                    builder.AppendLine($"  {type}");
            }

            // The graded claims block (U2): a test verdict is compiler/test-grade truth (the real dotnet test ran),
            // so this claim is verified - the strongest grade, distinct from the graph-grade impact claims.
            builder.AppendLine();
            builder.AppendLine(ClaimLedger.Render(
            [
                Claim.FromCompiler(
                    $"{passed} of {result.Verdicts.Count} covering test(s) passed ({failed} failed) for {symbol}",
                    "test: build-grade dotnet test run over the covering set"),
            ]));

            return builder.ToString().TrimEnd();
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    ///     Compiler-executed solution-wide rename (R7): renames a symbol and every reference through Roslyn and
    ///     returns the change as a staged diff, never touching the working tree. Answers only when the whole
    ///     solution loads (a partial rename is worse than none) and abstains otherwise.
    /// </summary>
    /// <param name="path">The workspace directory.</param>
    /// <param name="symbol">The simple name of the symbol to rename.</param>
    /// <param name="newName">The new name.</param>
    /// <param name="cancellationToken">A token to cancel the rename.</param>
    /// <returns>The staged per-file diffs, or an explicit abstention.</returns>
    [McpServerTool(Name = "fuse_refactor", ReadOnly = true)]
    [Description("Compiler-executed, verify-gated refactors returned as a staged diff (nothing is written to disk). operation=rename (default): rename a symbol and all its references through Roslyn (a same-named unrelated symbol is not touched). operation=add-parameter: add a trailing parameter to a method and its override/interface family, threading an explicit argument (the `argument` value) into every call site. operation=add-cancellation-token: add a CancellationToken parameter and thread an in-scope token into every call site that has one, listing token-less sites as manual follow-ups. operation=remove-parameter: remove a parameter (named by parameterName) and drop its argument at every call site, abstaining when the parameter is used in a body or a call site passes a non-trivial (possibly side-effecting) argument. operation=reorder-parameters: reorder parameters into `newOrder` (comma-separated names), abstaining if any call site uses positional arguments (only named-argument call sites are safe to reorder). operation=extract-interface: generate an interface from a class's public instance methods and properties (name it with newName, else I<Class>) and make the class implement it. operation=move-type: move a top-level type (symbol) to its own new file named after it, removing it from its current file. operation=apply-codefix: apply the repo's own analyzer code fix for `diagnosticId` in `file`, driving that diagnostic to zero (discovers the analyzers and [ExportCodeFixProvider] fixes from the project's analyzer references). The signature and type operations recompile the solution and return the diff ONLY when no new diagnostic is introduced; otherwise they abstain naming the offending sites (never a mostly-right diff). Rename and the signature ops answer only when the whole solution loads cleanly; abstain otherwise. Review the diff and re-check with fuse_check before applying.")]
    public static async Task<string> FuseRefactorAsync(
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("The simple name of the symbol to rename, or the method name for a signature operation.")] string symbol = "",
        [Description("The new name (rename only).")] string newName = "",
        [Description("The operation: rename (default), add-parameter, or add-cancellation-token.")] string operation = "rename",
        [Description("The declaring type's simple name, to disambiguate a method shared across types (signature operations).")] string containingType = "",
        [Description("The new parameter's type, as written in source (add-parameter).")] string parameterType = "",
        [Description("The new parameter's name (add-parameter; defaults to cancellationToken for add-cancellation-token).")] string parameterName = "",
        [Description("The argument expression added at every call site (add-parameter; defaults to 'default').")] string argument = "default",
        [Description("The parameter names in the desired order, comma-separated (reorder-parameters).")] string newOrder = "",
        [Description("The diagnostic id to fix (apply-codefix), for example IDE0090 or a repo analyzer id.")] string diagnosticId = "",
        [Description("The repo-relative file to apply the code fix in (apply-codefix).")] string file = "",
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(path);
        var discovery = await new Fuse.Semantics.DotNetWorkspaceDiscoverer().DiscoverAsync(root, cancellationToken);
        var target = discovery.SolutionPath ?? discovery.ProjectPaths.FirstOrDefault();
        if (target is null)
            return "cannot refactor: no solution or project found. fuse_refactor abstains.";

        var containing = string.IsNullOrWhiteSpace(containingType) ? null : containingType;
        switch (operation.Trim().ToLowerInvariant())
        {
            case "add-parameter":
                if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(parameterType) || string.IsNullOrWhiteSpace(parameterName))
                    return "Error: add-parameter needs the method (symbol), parameterType, and parameterName.";
                return RenderChangeSignature(
                    await new Fuse.Semantics.ChangeSignatureRefactorer().AddParameterAsync(
                        target, symbol, containing, parameterType, parameterName, argument, cancellationToken),
                    $"add parameter '{parameterType} {parameterName}' to {symbol}");

            case "add-cancellation-token":
                if (string.IsNullOrWhiteSpace(symbol))
                    return "Error: add-cancellation-token needs the method (symbol).";
                var tokenName = string.IsNullOrWhiteSpace(parameterName) ? "cancellationToken" : parameterName;
                return RenderChangeSignature(
                    await new Fuse.Semantics.ChangeSignatureRefactorer().ThreadCancellationTokenAsync(
                        target, symbol, containing, tokenName, cancellationToken),
                    $"thread a CancellationToken '{tokenName}' through {symbol}");

            case "remove-parameter":
                if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(parameterName))
                    return "Error: remove-parameter needs the method (symbol) and parameterName.";
                return RenderChangeSignature(
                    await new Fuse.Semantics.ChangeSignatureRefactorer().RemoveParameterAsync(
                        target, symbol, containing, parameterName, cancellationToken),
                    $"remove parameter '{parameterName}' from {symbol}");

            case "reorder-parameters":
                if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(newOrder))
                    return "Error: reorder-parameters needs the method (symbol) and newOrder (comma-separated parameter names).";
                var order = newOrder.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return RenderChangeSignature(
                    await new Fuse.Semantics.ChangeSignatureRefactorer().ReorderParametersAsync(
                        target, symbol, containing, order, cancellationToken),
                    $"reorder parameters of {symbol}");

            case "extract-interface":
                if (string.IsNullOrWhiteSpace(symbol))
                    return "Error: extract-interface needs the class (symbol).";
                return RenderTypeRefactor(
                    await new Fuse.Semantics.TypeRefactorer().ExtractInterfaceAsync(
                        target, symbol, string.IsNullOrWhiteSpace(newName) ? null : newName, cancellationToken),
                    $"extract interface from {symbol}");

            case "move-type":
                if (string.IsNullOrWhiteSpace(symbol))
                    return "Error: move-type needs the type (symbol).";
                return RenderTypeRefactor(
                    await new Fuse.Semantics.TypeRefactorer().MoveTypeToOwnFileAsync(target, symbol, cancellationToken),
                    $"move {symbol} to its own file");

            case "apply-codefix":
                if (string.IsNullOrWhiteSpace(diagnosticId) || string.IsNullOrWhiteSpace(file))
                    return "Error: apply-codefix needs a diagnosticId and a file.";
                var fixResult = await new Fuse.Semantics.CodeFixApplier().ApplyCodeFixAsync(target, diagnosticId, file, cancellationToken);
                if (!fixResult.Changed)
                    return $"cannot apply the fix for {diagnosticId} in {file}: {fixResult.Reason}";
                var fixBuilder = new StringBuilder();
                fixBuilder.AppendLine($"staged apply-codefix {fixResult.DiagnosticId} in {fixResult.FilePath} ({fixResult.Applied} fix(es) applied, verified clean, not written to disk)");
                fixBuilder.AppendLine($"--- {fixResult.FilePath} (full new content)");
                fixBuilder.AppendLine(fixResult.NewText);
                fixBuilder.AppendLine();
                fixBuilder.AppendLine("This diff verified clean (the target diagnostic reached zero with no new compile error). Review it and re-check with fuse_check before applying.");
                return fixBuilder.ToString().TrimEnd();

            case "rename":
            case "":
                if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(newName))
                    return "Error: provide the symbol to rename and the new name.";
                var result = await new Fuse.Semantics.RenameRefactorer().RenameAsync(target, symbol, newName, cancellationToken);
                if (!result.Renamed)
                    return $"cannot rename: {result.Reason}";

                var builder = new StringBuilder();
                builder.AppendLine($"staged rename: {result.OldName} -> {result.NewName} ({result.Diffs.Count} file(s) changed, not written to disk)");
                foreach (var d in result.Diffs)
                {
                    builder.AppendLine($"--- {d.FilePath}");
                    builder.AppendLine(d.UnifiedDiff);
                }

                builder.AppendLine();
                builder.AppendLine("Review this diff and re-check with fuse_check before applying; a rename crossing a boundary Roslyn does not see (a string, reflection) would surface as a diagnostic there.");
                return builder.ToString().TrimEnd();

            default:
                return $"Error: unknown operation '{operation}'. Use rename, add-parameter, or add-cancellation-token.";
        }
    }

    // Renders a package-upgrade analysis (F3): the breaking public-API changes between two versions, or an abstention.
    private static string RenderPackageUpgrade(Fuse.Retrieval.PackageUpgradeReport report)
    {
        if (!report.Available)
            return $"package upgrade {report.PackageId}: cannot analyze - {report.Reason}";

        var builder = new StringBuilder();
        var verdict = report.HasBreaking ? $"{report.BreakingChanges.Count} BREAKING public-API change(s)" : "no breaking public-API changes";
        builder.AppendLine($"package upgrade {report.PackageId}: {verdict}, {report.AdditiveChanges.Count} additive");
        foreach (var c in report.BreakingChanges)
            builder.AppendLine($"  BREAK [{c.Kind}] {c.Symbol}{(c.Before is null ? string.Empty : $"  (was: {c.Before})")}");
        if (report.BreakingChanges.Count == 0)
            builder.AppendLine("  (the target version keeps every public/protected member of the referenced version)");

        builder.AppendLine();
        builder.AppendLine(report.BlindSpots);
        return builder.ToString().TrimEnd();
    }

    // Renders a type-refactor outcome (extract-interface, move-type): the staged full-file content, or the abstention.
    private static string RenderTypeRefactor(Fuse.Semantics.TypeRefactorResult result, string what)
    {
        if (!result.Changed)
            return $"cannot {what}: {result.Reason}";

        var builder = new StringBuilder();
        builder.AppendLine($"staged {result.Summary} (verified clean; {result.Diffs.Count} file(s), not written to disk)");
        foreach (var d in result.Diffs)
        {
            builder.AppendLine($"--- {d.FilePath} (full new content)");
            builder.AppendLine(d.NewText);
        }

        builder.AppendLine();
        builder.AppendLine("This diff verified clean (no new compile diagnostic). Review it and re-check with fuse_check before applying.");
        return builder.ToString().TrimEnd();
    }

    // Renders a change-signature outcome: the staged diff plus any manual follow-up sites, or the abstention.
    private static string RenderChangeSignature(Fuse.Semantics.ChangeSignatureResult result, string what)
    {
        if (!result.Changed)
            return $"cannot {what}: {result.Reason}";

        var builder = new StringBuilder();
        builder.AppendLine($"staged {what}: {result.OldSignature} (added {result.Added}; {result.Diffs.Count} file(s) changed, not written to disk)");
        foreach (var d in result.Diffs)
        {
            builder.AppendLine($"--- {d.FilePath}");
            builder.AppendLine(d.UnifiedDiff);
        }

        if (result.ManualFollowUps.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"Manual follow-ups ({result.ManualFollowUps.Count} call site(s) had no in-scope value, so 'default' was passed; thread a real value):");
            foreach (var site in result.ManualFollowUps)
                builder.AppendLine($"  {site}");
        }

        builder.AppendLine();
        builder.AppendLine("This diff verified clean (no new compile diagnostic). Review it and re-check with fuse_check before applying.");
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    ///     Typechecks a proposed single-file edit and returns the compiler diagnostics the change would produce,
    ///     without writing the file (T0, Decision D11). Verification never shrugs: it answers at oracle-grade (a
    ///     speculative typecheck against the build-captured compilation, no build) when tier-1 is available, falls
    ///     back to build-grade (running <c>dotnet build</c> scoped to the owning project and parsing the same
    ///     diagnostic shape) otherwise, and abstains only when even the toolchain cannot run. Every answer is
    ///     stamped with its <see cref="Fuse.Indexing.CheckResult.Grade" />.
    /// </summary>
    /// <param name="indexer">The semantic indexer (opens the store for repair-packet context).</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="file">The repo-relative path of the file being changed.</param>
    /// <param name="content">The proposed full new content of that file.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>The diagnostics for the changed document, a clean verdict, or an explicit abstention.</returns>
    [McpServerTool(Name = "fuse_check", ReadOnly = true)]
    [Description("Speculatively typecheck a proposed single-file edit: the compiler errors and warnings it would produce, without writing the file. Verification never shrugs (D11): oracle-grade (sub-second, no build) when the repo is captured at tier-1; otherwise build-grade, running dotnet build scoped to the owning project (tens of seconds) and parsing the same diagnostics; abstains only when even the toolchain cannot run, naming the reason. Every answer is stamped with its grade. Delta mode (S2): pass a session id with no content to get the diagnostics your on-disk edits introduced or resolved since the session baseline (needs a resident workspace; does not run a build); full:true returns the whole current set; markGreen:true resets the baseline to now. Analyzer parity (S4): when a resident workspace serves the root, analyzers:true (the default) also runs the repo's configured analyzers and nullable warnings at their editorconfig severities, so a green check matches CI.")]
    public static async Task<string> FuseCheckAsync(
        SemanticIndexer indexer,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("The repo-relative path of the file being changed.")] string file = "",
        [Description("The proposed full new content of that file.")] string content = "",
        [Description("Delta mode: a session id. With no content, returns the diagnostics introduced or resolved since the session baseline (needs a resident workspace; does not run a build).")] string session = "",
        [Description("Delta mode: return the whole current diagnostic set instead of the delta since the baseline.")] bool full = false,
        [Description("Delta mode: reset the session baseline to the current diagnostics (mark green), so later deltas are measured from here.")] bool markGreen = false,
        [Description("Also run the repo's configured analyzers and nullable warnings at their editorconfig severities (CI parity), when a resident workspace serves the root. Default on.")] bool analyzers = true,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(path);

        // Delta mode (S2): a session with no proposed content asks "what did my last on-disk edit change", diffed
        // against the persisted session baseline. The content path below is unchanged when content is supplied.
        if (string.IsNullOrEmpty(content) && !string.IsNullOrWhiteSpace(session))
            return await FuseCheckDeltaAsync(indexer, path, root, session, full, markGreen, cancellationToken);

        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrEmpty(content))
            return "Error: provide the changed file path and its proposed new content (or a session id for delta mode).";

        var discovery = await new Fuse.Semantics.DotNetWorkspaceDiscoverer().DiscoverAsync(root, cancellationToken);

        // The verification-grade ladder (T0, D11): try oracle-grade first (speculative, resident/captured
        // compilation), fall back to build-grade (run the real toolchain, scoped to the owning project), and abstain
        // only when neither can run. An oracle abstention (tier-1 not configured, or the project did not load clean)
        // is a fall-through to build-grade, not a shrug.
        //
        // Resident-first (S1, D8): when a live resident workspace serves this root it answers the oracle check from
        // the held compilation (no per-check rebuild). With no resident workspace wired the provider returns null and
        // this is a no-op, so the build-capture-worker path below is unchanged. Analyzer parity (S4): the single-file
        // verify defaults analyzers on, so the check reports what CI's build step enforces, not just compiler errors.
        Fuse.Indexing.CheckResult? oracle = null;
        var residentDiagnostics = await ResidentWorkspaces.TryCheckOverlayAsync(root, file, content, analyzers, cancellationToken);
        if (residentDiagnostics is not null)
            oracle = Fuse.Indexing.CheckResult.Ok(residentDiagnostics);

        var client = new Fuse.Semantics.BuildCaptureClient();
        var oracleTarget = discovery.SolutionPath ?? discovery.ProjectPaths.FirstOrDefault();
        if (oracle is null && client.IsAvailable && oracleTarget is not null)
        {
            var candidate = await client.CheckAsync(oracleTarget, file, content, TimeSpan.FromMinutes(10), cancellationToken);
            if (candidate.Verified)
                oracle = candidate;
        }

        Fuse.Indexing.CheckResult result;
        long buildElapsedMs = 0;
        if (oracle is not null)
        {
            result = oracle;
        }
        else if (discovery.ProjectPaths.Count == 0)
        {
            return "cannot verify: no project found to build. fuse_check abstains (no oracle-grade capture and no buildable project).";
        }
        else
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            result = await new Fuse.Semantics.BuildGradeChecker().CheckAsync(root, discovery.ProjectPaths, file, content, cancellationToken);
            buildElapsedMs = stopwatch.ElapsedMilliseconds;
        }

        if (!result.Verified)
            return $"cannot verify ({result.Grade}): {result.Reason}";

        var gradeLine = result.Grade == "oracle"
            ? "verification grade: oracle (speculative typecheck, no build, no disk write)"
            : $"verification grade: build (ran dotnet build scoped to the owning project, {buildElapsedMs / 1000.0:F1}s, no disk write)";

        if (result.IsClean)
            return $"{gradeLine}\nclean: no errors in the changed document {file}.";

        var builder = new StringBuilder();
        builder.AppendLine(gradeLine);
        builder.AppendLine($"diagnostics for {file}: {result.Diagnostics.Count}");
        foreach (var d in result.Diagnostics)
            builder.AppendLine($"  {d.Severity} {d.Id} at line {d.Line}: {d.Message}");

        // Repair packets (R6): for the API-shape errors an agent most often hits, attach the fix context (the
        // receiver type's real members, or the nearest type names) from the persisted symbol table, so the fix
        // does not cost another round-trip. Best-effort: only diagnostics with a concrete suggestion add a packet.
        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var packetBuilder = new RepairPacketBuilder(store);
        var packets = new List<RepairPacket>();
        foreach (var d in result.Diagnostics.Where(d => d.Severity == "Error"))
        {
            var packet = await packetBuilder.BuildAsync(d, cancellationToken);
            if (packet is not null)
                packets.Add(packet);
        }

        if (packets.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("repair packets:");
            foreach (var p in packets)
            {
                builder.AppendLine($"  [{p.DiagnosticId}] {p.Explanation}");
                if (p.TopRepair is { } repair)
                    builder.AppendLine($"    apply: replace '{repair.OldToken}' with '{repair.NewToken}'");
                foreach (var m in p.Members.Take(12))
                    builder.AppendLine($"    {(string.IsNullOrEmpty(m.Signature) ? m.Name : m.Signature)}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    // Delta mode (S2): compute the diagnostics introduced or resolved since a persisted session baseline. Current
    // whole-state diagnostics come from a live resident workspace (delta mode must not run a build); the baseline
    // is persisted so a restarted process resumes it. On session start (no baseline) the current set is recorded as
    // the baseline; markGreen resets it to current; full returns the whole current set.
    private static async Task<string> FuseCheckDeltaAsync(
        SemanticIndexer indexer, string path, string root, string session, bool full, bool markGreen, CancellationToken cancellationToken)
    {
        var current = ResidentWorkspaces.TryGetCurrentDiagnostics(root);
        if (current is null)
        {
            return "cannot compute delta (abstain): no resident workspace serves this root, and delta mode does not run a build. "
                + "Start the server with FUSE_RESIDENT=1 for delta mode, or pass file and content for a speculative single-file check.";
        }

        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);

        if (markGreen)
        {
            await store.SaveCheckSessionBaselineAsync(session, root, current, cancellationToken);
            return $"delta mode: session '{session}' baseline reset (marked green) to {current.Count} current diagnostic(s). Later deltas are measured from here.";
        }

        if (full)
        {
            var all = new StringBuilder();
            all.AppendLine($"delta mode (resident): full diagnostic set, {current.Count} diagnostic(s).");
            foreach (var d in current)
                all.AppendLine($"  {d.Severity} {d.Id} {d.FilePath}:{d.Line}: {d.Message}");
            return all.ToString().TrimEnd();
        }

        var baseline = await store.GetCheckSessionBaselineAsync(session, cancellationToken);
        if (baseline is null)
        {
            await store.SaveCheckSessionBaselineAsync(session, root, current, cancellationToken);
            return $"delta mode: session '{session}' established with a {current.Count}-diagnostic baseline. Edit, then call again with the same session to see what your change introduced or resolved.";
        }

        var delta = DiagnosticDelta.Compute(baseline.Diagnostics, current);
        var builder = new StringBuilder();
        builder.AppendLine($"delta mode (resident, since baseline {baseline.UpdatedUtc}): {delta.Introduced.Count} introduced, {delta.Resolved.Count} resolved.");

        if (delta.Introduced.Count == 0 && delta.Resolved.Count == 0)
        {
            builder.AppendLine("  (no change in diagnostics since the baseline)");
            return builder.ToString().TrimEnd();
        }

        if (delta.Introduced.Count > 0)
        {
            builder.AppendLine("introduced (attributed to your changes since the baseline):");
            foreach (var d in delta.Introduced)
                builder.AppendLine($"  {d.Severity} {d.Id} {d.FilePath}:{d.Line}: {d.Message}");
        }

        if (delta.Resolved.Count > 0)
        {
            builder.AppendLine("resolved:");
            foreach (var d in delta.Resolved)
                builder.AppendLine($"  {d.Severity} {d.Id} {d.FilePath}:{d.Line}: {d.Message}");
        }

        // Repair packets (R6) for the introduced errors, so the fix does not cost another round-trip.
        var packetBuilder = new RepairPacketBuilder(store);
        var packets = new List<RepairPacket>();
        foreach (var d in delta.Introduced.Where(d => d.Severity == "Error"))
        {
            var packet = await packetBuilder.BuildAsync(d, cancellationToken);
            if (packet is not null)
                packets.Add(packet);
        }

        if (packets.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("repair packets:");
            foreach (var p in packets)
            {
                builder.AppendLine($"  [{p.DiagnosticId}] {p.Explanation}");
                if (p.TopRepair is { } repair)
                    builder.AppendLine($"    apply: replace '{repair.OldToken}' with '{repair.NewToken}'");
                foreach (var m in p.Members.Take(12))
                    builder.AppendLine($"    {(string.IsNullOrEmpty(m.Signature) ? m.Name : m.Signature)}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    ///     The speculative staging area (M1): stage single-file edits into a changeset session, diagnose them
    ///     with the speculative typecheck, select the covering tests, then promote the diff or discard it. The
    ///     working tree is touched only on an explicit promote.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use, for the select operation).</param>
    /// <param name="op">The lifecycle operation: create, stage, list, diagnose, select, promote, or discard.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="session">The session id (required for every op except create).</param>
    /// <param name="file">The repo-relative file path (for stage).</param>
    /// <param name="content">The proposed full new content of the file (for stage).</param>
    /// <param name="limit">The maximum covering tests to return (for select).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The operation's result, or an error/abstention string.</returns>
    // Dissolved in U1: the changeset workflow is now check-with-content (diagnose) + fuse_refactor (edits) +
    // fuse_workspace action=apply (write, D2). The method is retained dormant behind the shim for one major.
    public static async Task<string> FuseChangesetAsync(
        SemanticIndexer indexer,
        [Description("The lifecycle operation: create, stage, list, diagnose, select, promote, or discard.")] string op = "",
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("The session id (required for every op except create).")] string? session = null,
        [Description("The repo-relative file path (for stage).")] string? file = null,
        [Description("The proposed full new content of the file (for stage).")] string? content = null,
        [Description("Maximum covering tests to return (for select).")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(path);
        var store = FuseTools.ChangesetSessions;
        switch (op.Trim().ToLowerInvariant())
        {
            case "create":
                return $"session {store.Create(root)} created. Stage edits, then diagnose/select, then promote or discard.";

            case "stage":
                if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(file) || content is null)
                    return "Error: stage needs session, file, and content.";
                return store.Stage(session, file, content)
                    ? $"staged {file} in session {session} (not written to disk)."
                    : $"unknown session {session}.";

            case "list":
                var staged = session is null ? null : store.StagedFiles(session);
                return staged is null ? $"unknown session {session}." : $"staged in {session}: {string.Join(", ", staged)}";

            case "diagnose":
                if (string.IsNullOrWhiteSpace(session))
                    return "Error: diagnose needs session.";
                var target = await DiscoverBuildTargetAsync(root, cancellationToken);
                if (target is null)
                    return "cannot verify: no solution or project found to build. fuse_changeset diagnose abstains.";
                var diagnoses = await store.DiagnoseAsync(
                    session, target, new Fuse.Semantics.BuildCaptureClient(), TimeSpan.FromMinutes(10), cancellationToken,
                    // Resident-first (S1): when a live resident workspace serves this root, each staged file is
                    // diagnosed oracle-grade from the held compilation with no build; else the build-capture path runs.
                    (file, content) => ResidentWorkspaces.TryCheckOverlay(root, file, content, cancellationToken));
                if (diagnoses is null)
                    return $"unknown session {session}.";
                return RenderDiagnoses(diagnoses);

            case "select":
                if (string.IsNullOrWhiteSpace(session))
                    return "Error: select needs session.";
                await using (var idx = await OpenIndexedAsync(indexer, path, cancellationToken))
                {
                    var tests = await store.SelectCoveringTestsAsync(session, idx, new GraphNeighborhoodExplorer(idx), limit, cancellationToken);
                    if (tests is null)
                        return $"unknown session {session}.";
                    return tests.Count == 0
                        ? "covering tests: 0 (a lower bound from R5 tests edges; none reached the changed symbols)"
                        : $"covering tests ({tests.Count}, a lower bound; run with your own --filter):\n  " + string.Join("\n  ", tests);
                }

            case "promote":
                if (string.IsNullOrWhiteSpace(session))
                    return "Error: promote needs session.";
                var written = await store.PromoteAsync(session, cancellationToken);
                return written is null
                    ? $"unknown session {session}."
                    : $"promoted session {session}: wrote {written.Count} file(s) to the working tree: {string.Join(", ", written)}";

            case "discard":
                if (string.IsNullOrWhiteSpace(session))
                    return "Error: discard needs session.";
                return store.Discard(session)
                    ? $"discarded session {session}; the working tree is untouched."
                    : $"unknown session {session}.";

            default:
                return "Error: op must be one of create, stage, list, diagnose, select, promote, discard.";
        }
    }

    private static async Task<string?> DiscoverBuildTargetAsync(string root, CancellationToken cancellationToken)
    {
        var discovery = await new Fuse.Semantics.DotNetWorkspaceDiscoverer().DiscoverAsync(root, cancellationToken);
        return discovery.SolutionPath ?? discovery.ProjectPaths.FirstOrDefault();
    }

    private static string RenderDiagnoses(IReadOnlyList<Fuse.Retrieval.ChangesetDiagnosis> diagnoses)
    {
        var builder = new StringBuilder();
        foreach (var d in diagnoses)
        {
            if (!d.Check.Verified)
                builder.AppendLine($"{d.File}: cannot verify ({d.Check.Reason}).");
            else if (d.Check.IsClean)
                builder.AppendLine($"{d.File}: clean (no errors in the changed document).");
            else
            {
                builder.AppendLine($"{d.File}: {d.Check.Diagnostics.Count} diagnostic(s):");
                foreach (var diag in d.Check.Diagnostics)
                    builder.AppendLine($"  {diag.Severity} {diag.Id} at line {diag.Line}: {diag.Message}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    ///     Deterministically resolves .NET wiring to its target(s): no source bodies.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="service">A service to resolve to its implementation.</param>
    /// <param name="request">A request/command to resolve to its handler.</param>
    /// <param name="route">A route to resolve to its action.</param>
    /// <param name="config">A config section to resolve to its options type.</param>
    /// <param name="symbol">A symbol to resolve to its declaration.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The resolved target(s) with paths and evidence.</returns>
    // Folded into fuse_find (kind=service/request/route/config) in U1; kept as an internal helper, shimmed by name.
    public static async Task<string> FuseResolveAsync(
        SemanticIndexer indexer,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("A service to resolve to its implementation.")] string? service = null,
        [Description("A request/command to resolve to its handler.")] string? request = null,
        [Description("A route to resolve, for example \"POST /api/orders/{id}\".")] string? route = null,
        [Description("A config section to resolve to its options type.")] string? config = null,
        [Description("A symbol name to resolve to its declaration.")] string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var resolver = new SemanticResolver(store);

        ResolveResult? result = null;
        if (!string.IsNullOrWhiteSpace(service))
            result = await resolver.ResolveServiceAsync(service, cancellationToken);
        else if (!string.IsNullOrWhiteSpace(request))
            result = await resolver.ResolveRequestAsync(request, cancellationToken);
        else if (!string.IsNullOrWhiteSpace(route))
            result = await resolver.ResolveRouteAsync(route, cancellationToken);
        else if (!string.IsNullOrWhiteSpace(config))
            result = await resolver.ResolveConfigAsync(config, cancellationToken);
        else if (!string.IsNullOrWhiteSpace(symbol))
            result = await resolver.ResolveSymbolAsync(symbol, cancellationToken);

        if (result is null)
            return "Error: specify one of service, request, route, config, or symbol.";

        var builder = new StringBuilder();
        builder.AppendLine($"resolve {result.Target.ToString().ToLowerInvariant()}: {result.Query}");
        if (result.Matches.Count == 0)
            builder.AppendLine("  no matches");
        foreach (var match in result.Matches)
        {
            var location = match.FilePath is null ? string.Empty : $"  ({match.FilePath}:{match.StartLine})";
            builder.AppendLine($"  [{match.Relation}] {match.Kind} {match.DisplayName}{location}");
            if (match.Signature is not null)
                builder.AppendLine($"      {match.Signature}");
        }

        // The graded claims block (U2): a wiring resolution rests on the persisted graph, so it caps at
        // partially_verified (real signal, not compiler-confirmed).
        builder.AppendLine();
        builder.AppendLine(ClaimLedger.Render(
        [
            Claim.FromGraph(
                $"{result.Query} resolves to {result.Matches.Count} {result.Target.ToString().ToLowerInvariant()} target(s)",
                $"graph: {result.Target.ToString().ToLowerInvariant()} wiring edges"),
        ]));

        return builder.ToString();
    }

    /// <summary>
    ///     Plans and emits context for a set of seeds, with source bodies at mixed render tiers.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="reductionPipeline">The reduction pipeline used to render bodies.</param>
    /// <param name="sessionStore">The session store used to elide unchanged files.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="seeds">Symbol seeds.</param>
    /// <param name="files">File path seeds (for example paths from localize).</param>
    /// <param name="services">Service seeds to resolve and expand.</param>
    /// <param name="requests">Request/command seeds to resolve and expand.</param>
    /// <param name="configs">Config section seeds to resolve and expand.</param>
    /// <param name="routes">Route seeds.</param>
    /// <param name="depth">The graph expansion depth.</param>
    /// <param name="maxTokens">The token budget.</param>
    /// <param name="format">The output format: xml, markdown, or json.</param>
    /// <param name="sessionId">Session id; files already sent unchanged in the session are elided.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The emitted context payload.</returns>
    [McpServerTool(Name = "fuse_context", ReadOnly = true)]
    [Description("Plan and emit context (source bodies, mixed render tiers, manifest, provenance) for a set of seeds. Feed it the file paths from fuse_localize or the names from fuse_resolve. Pass a sessionId to elide files already sent in the session.")]
    public static async Task<string> FuseContextAsync(
        SemanticIndexer indexer,
        ContentReductionPipeline reductionPipeline,
        ContextSessionStore sessionStore,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("Symbol seeds.")] string[]? seeds = null,
        [Description("File path seeds (for example the paths returned by fuse_localize).")] string[]? files = null,
        [Description("Service seeds to resolve and expand.")] string[]? services = null,
        [Description("Request/command seeds to resolve and expand.")] string[]? requests = null,
        [Description("Config section seeds to resolve and expand.")] string[]? configs = null,
        [Description("Route seeds, for example \"POST /api/orders/{id}\".")] string[]? routes = null,
        [Description("Graph expansion depth.")] int depth = 2,
        [Description("Token budget; must-keep seeds are always included.")] int maxTokens = 0,
        [Description("Output format: xml (default), markdown, or json.")] string format = "xml",
        [Description("Session id; files already sent unchanged in this session are elided.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var seedList = BuildSeeds(seeds, files, services, requests, configs, routes);
        if (seedList.Count == 0)
            return "Error: provide at least one seed (symbol/file/service/request/config/route).";

        var root = Path.GetFullPath(path);
        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var engine = new SemanticRetrievalEngine(store);
        var plan = await engine.PlanContextAsync(
            new ContextRequest(root, seedList, depth, maxTokens > 0 ? maxTokens : null), cancellationToken);

        var renderer = new SemanticContextRenderer(reductionPipeline, new SourceContentProvider(new PhysicalFileSystem()));
        var rendered = await renderer.RenderAsync(plan, root, cancellationToken);
        var unchanged = string.IsNullOrWhiteSpace(sessionId) ? null : sessionStore.Reconcile(sessionId, rendered.Files);
        return SemanticContextEmitter.Emit(plan, rendered, ParseFormat(format), root, unchangedPaths: unchanged);
    }

    private static List<ContextSeed> BuildSeeds(string[]? seeds, string[]? files, string[]? services, string[]? requests, string[]? configs, string[]? routes) =>
        (seeds ?? []).Select(s => new ContextSeed(ContextSeedKind.Symbol, s))
            .Concat((files ?? []).Select(f => new ContextSeed(ContextSeedKind.File, f)))
            .Concat((services ?? []).Select(s => new ContextSeed(ContextSeedKind.Service, s)))
            .Concat((requests ?? []).Select(r => new ContextSeed(ContextSeedKind.Request, r)))
            .Concat((configs ?? []).Select(c => new ContextSeed(ContextSeedKind.Config, c)))
            .Concat((routes ?? []).Select(r => new ContextSeed(ContextSeedKind.Route, r)))
            .ToList();

    /// <summary>
    ///     Reviews the semantic impact of a change and emits the packed context.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="reductionPipeline">The reduction pipeline used to render bodies.</param>
    /// <param name="changeSource">The change source for resolving the git base ref.</param>
    /// <param name="sessionStore">The session store used to elide unchanged files.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="changedSince">The git base ref to diff against.</param>
    /// <param name="maxTokens">The token budget.</param>
    /// <param name="includeTests">Whether to include related test files.</param>
    /// <param name="format">The output format: xml, markdown, or json.</param>
    /// <param name="sessionId">Session id; files already sent unchanged in the session are elided.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The review preamble plus the emitted context payload.</returns>
    [McpServerTool(Name = "fuse_review", ReadOnly = true)]
    [Description("Review the semantic impact of a change since a git base ref: changed files, the blast radius (callers, DI consumers, route/request handlers, options consumers, tests), and the packed context. The flagship tool for PR/change work.")]
    public static async Task<string> FuseReviewAsync(
        SemanticIndexer indexer,
        ContentReductionPipeline reductionPipeline,
        IChangeSource changeSource,
        ContextSessionStore sessionStore,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("The git base ref to diff against (branch, commit, or HEAD~N).")] string changedSince = "HEAD",
        [Description("Token budget; changed files are always kept.")] int maxTokens = 0,
        [Description("Include related test files.")] bool includeTests = true,
        [Description("Output format: xml (default), markdown, or json.")] string format = "xml",
        [Description("Session id; files already sent unchanged in this session are elided.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(path);
        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var engine = new SemanticRetrievalEngine(store, changeSource);
        var plan = await engine.ReviewAsync(
            new ReviewRequest(root, changedSince, MaxTokens: maxTokens > 0 ? maxTokens : null, IncludeTests: includeTests),
            cancellationToken);

        var renderer = new SemanticContextRenderer(reductionPipeline, new SourceContentProvider(new PhysicalFileSystem()));
        var rendered = await renderer.RenderAsync(plan, root, cancellationToken);
        var unchanged = string.IsNullOrWhiteSpace(sessionId) ? null : sessionStore.Reconcile(sessionId, rendered.Files);

        var apiDeltaSection = await BuildApiDeltaSectionAsync(changeSource, root, changedSince, cancellationToken);
        return SemanticContextEmitter.Emit(plan, rendered, ParseFormat(format), root, changedSince, unchanged, apiDeltaSection);
    }

    // The T2 public-API delta section for a review: added, removed, and changed public/protected members between
    // the base ref and the working tree. Best-effort - a git or read failure returns null so the review payload is
    // unaffected (the delta is an added section, not the review itself). The base side is read from the git base
    // ref; the current side from the working tree.
    private static async Task<string?> BuildApiDeltaSectionAsync(
        IChangeSource changeSource, string root, string changedSince, CancellationToken cancellationToken)
    {
        try
        {
            var changed = await changeSource.GetChangedFilesAsync(root, changedSince, cancellationToken);
            var delta = await ChangedApiSurfaceGatherer.GatherAsync(
                changeSource, root, changedSince, changed,
                (relativePath, _) =>
                {
                    var absolute = Path.Combine(root, relativePath);
                    return Task.FromResult(File.Exists(absolute) ? File.ReadAllText(absolute) : null);
                },
                cancellationToken);

            return delta.Changes.Count == 0 ? null : ApiDeltaReport.Render(delta);
        }
        catch (ChangeSourceException)
        {
            return null;
        }
    }

    // The T2 public-surface line for impact: whether the target symbol is on the public/protected API surface, so
    // an agent knows before editing whether a change is contract-relevant. Conservative (the T2 kill-risk): a
    // positive "public" is asserted only from IsPublicApi, which is reliable for types in any mode and for members
    // at the semantic tier; a member in syntax mode, where accessibility is unresolved, is reported undetermined
    // rather than guessed either way.
    private static async Task<string> BuildImpactApiSurfaceLineAsync(
        WorkspaceIndexStore store, string symbol, string mode, CancellationToken cancellationToken)
    {
        var matches = await store.FindSymbolsByNameAsync(symbol, 50, cancellationToken);
        var exact = matches
            .Where(m => string.Equals(m.Name, symbol, StringComparison.Ordinal)
                || string.Equals(m.FullyQualifiedName, symbol, StringComparison.Ordinal)
                || m.FullyQualifiedName.EndsWith("." + symbol, StringComparison.Ordinal))
            .ToList();

        if (exact.Count == 0)
            return $"public API surface: no indexed symbol named {symbol} to classify.";

        if (exact.Any(m => m.IsPublicApi))
        {
            return $"public API surface: {symbol} is on the public/protected surface (T2); removing it, reducing its "
                + "accessibility, or changing its signature is a breaking change, so the blast radius above is external-facing.";
        }

        // Not flagged public. For a member in syntax mode the flag is not reliable, so do not assert "internal".
        var allMembers = exact.All(m => !IsTypeKind(m.Kind));
        if (mode == "syntax" && allMembers)
        {
            return $"public API surface: undetermined for {symbol} in syntax mode (member accessibility is not "
                + "resolved); run fuse_index on a semantically loadable checkout to classify.";
        }

        return $"public API surface: {symbol} is not on the public/protected surface; a change is internal to the assembly.";
    }

    private static bool IsTypeKind(string kind) =>
        kind is "class" or "interface" or "struct" or "record" or "enum" or "delegate" or "type";

    private static ContextOutputFormat ParseFormat(string format) => format.Trim().ToLowerInvariant() switch
    {
        "markdown" or "md" => ContextOutputFormat.Markdown,
        "json" => ContextOutputFormat.Json,
        _ => ContextOutputFormat.Xml,
    };
}
