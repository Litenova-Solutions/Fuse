using Fuse.Indexing;
using Fuse.Retrieval;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Fuse.Benchmarks;

/// <summary>
///     Suite H2 (DiagBench): grades the repair-packet protocol itself, deterministically and without a model. For
///     each API-shape mutant (a misspelled member or type reference, the class of error a `fuse_check` packet
///     carries a machine-applicable repair for), it builds the shipped <see cref="RepairPacket" />, auto-applies
///     the packet's <c>TopRepair</c>, recompiles, and records whether the target diagnostic went to zero with no
///     new error. The output is the auto-fix rate per diagnostic id in <c>diagbench.json</c> - a feedback number
///     on the S2 packet work, not a product behavior (the product never auto-applies; an agent asks first).
/// </summary>
/// <remarks>
///     Fully in-process and deterministic: a raw-Roslyn baseline compilation (no MSBuild, no build-capture
///     closure) plus a temp store populated directly with the fixture's symbols, so <see cref="RepairPacketBuilder" />
///     resolves members exactly as it does in production. Mutants are single-token near-misses (drop a trailing
///     character, transpose the last two) of real member and type references, kept only when they compile to a
///     single packet-bearing diagnostic in the edited file, so the suite measures the nearest-name repair
///     heuristic against edits whose correct fix is known.
/// </remarks>
public sealed class DiagBenchSuite : IEvalSuite
{
    private const string Namespace = "Shop";
    private const string BaselineConsumer = """
        namespace Shop;
        public sealed class Consumer
        {
            public decimal UseTotal(Order o) => o.Total;
            public decimal UseSubtotal(Order o) => o.Subtotal;
            public decimal UseTax(Order o) => o.Tax;
            public int UseLineCount(Order o) => o.LineCount;
            public decimal UseDiscount(Order o) => o.Discount();
            public int UseNumber(Invoice i) => i.Number;
            public string UseName(Customer c) => c.Name;
            public Order MakeOrder() => new Order();
            public Invoice MakeInvoice() => new Invoice();
            public Customer MakeCustomer() => new Customer();
        }
        """;

    private const string BaselineModel = """
        namespace Shop;
        public sealed class Order
        {
            public decimal Total => 0m;
            public decimal Subtotal => 0m;
            public decimal Tax => 0m;
            public int LineCount => 0;
            public decimal Discount() => 0m;
        }
        public sealed class Invoice { public int Number => 0; }
        public sealed class Customer { public string Name => ""; }
        """;

    // The identifiers to perturb, each paired with the diagnostic id its misspelling produces: a member access
    // yields CS1061, a type reference yields CS0246.
    private static readonly (string Token, string DiagnosticId)[] Targets =
    [
        ("Total", "CS1061"), ("Subtotal", "CS1061"), ("Tax", "CS1061"), ("LineCount", "CS1061"),
        ("Discount", "CS1061"), ("Number", "CS1061"), ("Name", "CS1061"),
        ("Order", "CS0246"), ("Invoice", "CS0246"), ("Customer", "CS0246"),
    ];

    /// <inheritdoc />
    public string Name => "diagbench";

    /// <inheritdoc />
    public string Description => "Suite H2: repair-packet auto-apply (TopRepair) fix rate per diagnostic id.";

    /// <inheritdoc />
    public async Task<SuiteResult> RunAsync(EvalOptions options, CancellationToken cancellationToken)
    {
        var notes = new List<string>
        {
            "In-process: raw-Roslyn baseline + a store populated with the fixture's symbols; grades the shipped RepairPacket TopRepair.",
        };
        var tasks = new List<TaskResult>();

        var databasePath = Path.Combine(Path.GetTempPath(), "fuse-diagbench", Guid.NewGuid().ToString("N"), "fuse.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var store = new WorkspaceIndexStore(databasePath);
        await store.InitializeAsync(cancellationToken);
        await PopulateStoreAsync(store, cancellationToken);
        var builder = new RepairPacketBuilder(store);

        var references = FrameworkReferences();
        var baseline = Compile(BaselineModel, BaselineConsumer, references);
        var baselineErrors = baseline.GetDiagnostics(cancellationToken)
            .Count(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        if (baselineErrors != 0)
        {
            notes.Add($"Baseline has {baselineErrors} error(s); cannot run DiagBench.");
            return new SuiteResult(Name, Description, null, new Scorecard(0, 0, 0, 0, 0, 0, 0, 0), tasks, notes);
        }

        var perId = new Dictionary<string, (int Total, int Packeted, int Fixed)>(StringComparer.Ordinal);
        var mutantCount = 0;
        foreach (var (token, expectedId) in Targets)
        {
            foreach (var typo in Typos(token))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Perturb a single occurrence, so a type reference used in several places yields one CS0246 rather
                // than a cascade; the typo token is then unique, so the repair (replace-all of the typo) is exact.
                var mutantConsumer = ReplaceFirstWholeWord(BaselineConsumer, token, typo);
                if (mutantConsumer == BaselineConsumer)
                    continue;

                var mutant = Compile(BaselineModel, mutantConsumer, references);
                var errors = mutant.GetDiagnostics(cancellationToken)
                    .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                    .ToList();

                // Keep only the clean single-target case: exactly one error, of the expected packet-bearing id.
                var target = errors.FirstOrDefault(d => d.Id == expectedId);
                if (target is null || errors.Count != 1)
                    continue;

                mutantCount++;
                var entry = perId.GetValueOrDefault(expectedId);
                entry.Total++;

                var diagnostic = new CheckDiagnostic(
                    target.Id, "Error",
                    target.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
                    "Consumer.cs", target.Location.GetLineSpan().StartLinePosition.Line + 1);
                var packet = await builder.BuildAsync(diagnostic, cancellationToken);

                var fixedIt = false;
                if (packet?.TopRepair is { } repair)
                {
                    entry.Packeted++;
                    var repaired = ReplaceWholeWord(mutantConsumer, repair.OldToken, repair.NewToken);
                    var recompiled = Compile(BaselineModel, repaired, references);
                    var afterErrors = recompiled.GetDiagnostics(cancellationToken)
                        .Count(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
                    fixedIt = afterErrors == 0;
                    if (fixedIt)
                        entry.Fixed++;
                }

                perId[expectedId] = entry;
                tasks.Add(new TaskResult($"{token}->{typo}", "diagbench", expectedId, fixedIt ? 1.0 : 0.0, 1.0, 0, 0, new TaskFiles([], [], [])));
            }
        }

        foreach (var (id, e) in perId.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var rate = e.Packeted > 0 ? (double)e.Fixed / e.Packeted : 0.0;
            notes.Add($"{id}: {e.Total} mutant(s), {e.Packeted} carried a TopRepair, {e.Fixed} auto-fixed (fix rate {rate:P0} of packeted).");
        }

        var totalPacketed = perId.Values.Sum(e => e.Packeted);
        var totalFixed = perId.Values.Sum(e => e.Fixed);
        var overall = totalPacketed > 0 ? (double)totalFixed / totalPacketed : 0.0;
        notes.Add($"overall: {mutantCount} API-shape mutants, {totalPacketed} packeted, {totalFixed} auto-fixed ({overall:P0}).");

        // Recall carries the auto-fix rate (of packeted mutants); precision carries the packet coverage (packeted
        // of total), reusing the scorecard columns.
        var coverage = mutantCount > 0 ? (double)totalPacketed / mutantCount : 0.0;
        var scorecard = new Scorecard(mutantCount, overall, overall, overall, coverage, overall, 0, 0);
        return new SuiteResult(Name, Description, null, scorecard, tasks, notes);
    }

    private static async Task PopulateStoreAsync(WorkspaceIndexStore store, CancellationToken cancellationToken)
    {
        await store.UpsertFilesAsync(
            [new IndexedFileRecord("Model.cs", "Model.cs", ".cs", 200, 1, "h1")], cancellationToken);

        SymbolRecord Type(string name) => new($"symbol:{Namespace}.{name}", "Model.cs", "class", name, $"{Namespace}.{name}",
            Accessibility: "public", Signature: $"public sealed class {name}", StartLine: 1, EndLine: 1, IsPublicApi: true);
        SymbolRecord Member(string type, string name, string kind, string sig) =>
            new($"symbol:{Namespace}.{type}.{name}", "Model.cs", kind, name, $"{Namespace}.{type}.{name}",
                ContainingType: $"{Namespace}.{type}", Accessibility: "public", Signature: sig, StartLine: 1, EndLine: 1, IsPublicApi: true);

        await store.UpsertSymbolsAsync(
            [
                Type("Order"), Type("Invoice"), Type("Customer"),
                Member("Order", "Total", "property", "public decimal Total { get; }"),
                Member("Order", "Subtotal", "property", "public decimal Subtotal { get; }"),
                Member("Order", "Tax", "property", "public decimal Tax { get; }"),
                Member("Order", "LineCount", "property", "public int LineCount { get; }"),
                Member("Order", "Discount", "method", "public decimal Discount()"),
                Member("Invoice", "Number", "property", "public int Number { get; }"),
                Member("Customer", "Name", "property", "public string Name { get; }"),
            ],
            cancellationToken);
    }

    // Single-token near-misses: drop the trailing character, and transpose the last two characters. Both are one
    // edit from the original, so the nearest-name heuristic should recover it when it works.
    private static IEnumerable<string> Typos(string token)
    {
        if (token.Length >= 3)
            yield return token[..^1];
        if (token.Length >= 3)
            yield return token[..^2] + token[^1] + token[^2];
    }

    private static string ReplaceWholeWord(string source, string word, string replacement) =>
        System.Text.RegularExpressions.Regex.Replace(source, $@"\b{System.Text.RegularExpressions.Regex.Escape(word)}\b", replacement);

    private static string ReplaceFirstWholeWord(string source, string word, string replacement)
    {
        var regex = new System.Text.RegularExpressions.Regex($@"\b{System.Text.RegularExpressions.Regex.Escape(word)}\b");
        return regex.Replace(source, replacement, 1);
    }

    private static CSharpCompilation Compile(string model, string consumer, IEnumerable<MetadataReference> references) =>
        CSharpCompilation.Create(
            "DiagBench",
            [
                CSharpSyntaxTree.ParseText(model, path: "Model.cs"),
                CSharpSyntaxTree.ParseText(consumer, path: "Consumer.cs"),
            ],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static IEnumerable<MetadataReference> FrameworkReferences()
    {
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty;
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                yield return MetadataReference.CreateFromFile(path);
    }
}
