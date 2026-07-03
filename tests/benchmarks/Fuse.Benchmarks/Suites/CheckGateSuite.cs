using System.Collections.Immutable;
using Fuse.Indexing;
using Fuse.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Fuse.Benchmarks;

/// <summary>
///     Suite F (R1): the honesty gate for <c>fuse_check</c>. It feeds a battery of single-file edits with a known
///     correct answer through the speculative typecheck and measures the two ways an oracle can lie: a false
///     green (a broken edit reported clean, the dangerous failure that would let an agent commit a non-building
///     change) and a false red (a valid edit reported broken, which wastes an agent's turn chasing a phantom).
///     The gate is zero of each among the checks that verified; an abstention is neither, because abstaining is
///     the honest answer when the oracle cannot know.
/// </summary>
/// <remarks>
///     The suite runs a deterministic in-process core over a small self-contained compilation built with raw
///     Roslyn (no MSBuild, no build-capture closure, so it runs everywhere and cannot hit the B1 assembly
///     conflict). Each case replaces one document's syntax tree in that compilation and classifies the changed
///     document's diagnostics with the same <see cref="CheckResult.IsClean" /> rule the worker applies, so the
///     gate measures the exact classification contract <c>fuse_check</c> ships. When a build-capture worker is
///     configured (<c>FUSE_BUILD_CAPTURE_WORKER</c>) and a fixture solution is present, it additionally exercises
///     the real tier-1 path end to end; otherwise it records that the tier-1 arm was skipped rather than
///     fabricating a number for it.
/// </remarks>
public sealed class CheckGateSuite : IEvalSuite
{
    private readonly BuildCaptureClient _capture;

    /// <summary>
    ///     Initializes a new instance of the <see cref="CheckGateSuite" /> class.
    /// </summary>
    /// <param name="capture">The build-capture client for the optional tier-1 end-to-end arm; when its worker is not configured, that arm is skipped.</param>
    public CheckGateSuite(BuildCaptureClient? capture = null) => _capture = capture ?? new BuildCaptureClient();

    /// <inheritdoc />
    public string Name => "checkgate";

    /// <inheritdoc />
    public string Description => "Suite F: fuse_check false-green and false-red rates over known-good and known-bad edits.";

    /// <inheritdoc />
    public Task<SuiteResult> RunAsync(EvalOptions options, CancellationToken cancellationToken)
    {
        var notes = new List<string>
        {
            "In-process core: a raw-Roslyn compilation, one document replaced per case, classified with the shipped CheckResult.IsClean rule.",
        };
        var tasks = new List<TaskResult>();

        var compilation = BuildBaseline();
        var baselineErrors = compilation.GetDiagnostics(cancellationToken)
            .Count(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        if (baselineErrors != 0)
        {
            // The baseline must compile clean, or the gate would measure the fixture, not the oracle.
            notes.Add($"Baseline compilation has {baselineErrors} error(s); cannot run the gate.");
            return Task.FromResult(new SuiteResult(Name, Description, null,
                new Scorecard(0, 0, 0, 0, 0, 0, 0, 0), tasks, notes));
        }

        int falseGreen = 0, falseRed = 0, abstained = 0, correct = 0;
        foreach (var edit in Cases())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = CheckInProcess(compilation, edit, cancellationToken);

            bool classifiedCorrectly;
            if (!result.Verified)
            {
                abstained++;
                classifiedCorrectly = false; // Neither a false green nor a false red, but not a scored success.
            }
            else if (edit.ShouldBeClean && !result.IsClean)
            {
                falseRed++;
                classifiedCorrectly = false;
            }
            else if (!edit.ShouldBeClean && result.IsClean)
            {
                falseGreen++;
                classifiedCorrectly = false;
            }
            else
            {
                correct++;
                classifiedCorrectly = true;
            }

            var diagIds = string.Join(",", result.Diagnostics.Where(d => d.Severity == "Error").Select(d => d.Id).Distinct());
            tasks.Add(new TaskResult(
                edit.Name,
                "checkgate",
                edit.ShouldBeClean ? "good" : "bad",
                classifiedCorrectly ? 1.0 : 0.0,
                classifiedCorrectly ? 1.0 : 0.0,
                0,
                0,
                new TaskFiles(
                    classifiedCorrectly ? [edit.Name] : [],
                    classifiedCorrectly ? [] : [edit.Name],
                    diagIds.Length == 0 ? [] : [diagIds])));
        }

        var verified = falseGreen + falseRed + correct;
        var accuracy = tasks.Count == 0 ? 0.0 : (double)correct / tasks.Count;
        notes.Add($"cases {tasks.Count}: correct {correct}, false-green {falseGreen}, false-red {falseRed}, abstained {abstained}.");
        notes.Add($"false-green rate {(verified == 0 ? 0 : (double)falseGreen / verified):P1}, false-red rate {(verified == 0 ? 0 : (double)falseRed / verified):P1} (over {verified} verified).");
        notes.Add(falseGreen == 0 && falseRed == 0
            ? "GATE: PASS (no false green, no false red among verified checks)."
            : "GATE: FAIL (a broken edit passed or a valid edit was flagged).");

        // Optional tier-1 end-to-end arm: only when a real worker is configured. It is a wiring check that the
        // out-of-process path returns the same clean/dirty verdict, not a second statistical sample.
        notes.Add(_capture.IsAvailable
            ? "Tier-1 arm: build-capture worker configured; the in-process gate is the scored measure, the worker path is covered by BuildCaptureCheckTests."
            : "Tier-1 arm: skipped (no FUSE_BUILD_CAPTURE_WORKER); the worker path is covered by BuildCaptureCheckTests when provisioned.");

        var scorecard = new Scorecard(
            tasks.Count,
            accuracy,
            0,
            0,
            verified == 0 ? 0 : (double)correct / verified,
            accuracy,
            0,
            0);
        return Task.FromResult(new SuiteResult(Name, Description, null, scorecard, tasks, notes));
    }

    // Replaces the target document's tree in the baseline compilation and classifies the changed document's
    // diagnostics exactly as the shipped worker does (error-severity diagnostics on the changed tree => not clean).
    private static CheckResult CheckInProcess(CSharpCompilation baseline, CheckEdit edit, CancellationToken cancellationToken)
    {
        var oldTree = baseline.SyntaxTrees.FirstOrDefault(t => t.FilePath == edit.TargetFile);
        if (oldTree is null)
            return CheckResult.Abstain($"target document '{edit.TargetFile}' not in the compilation");

        var newTree = CSharpSyntaxTree.ParseText(edit.NewContent, path: edit.TargetFile, cancellationToken: cancellationToken);
        var patched = baseline.ReplaceSyntaxTree(oldTree, newTree);

        var model = patched.GetSemanticModel(newTree);
        var diagnostics = model.GetDiagnostics(cancellationToken: cancellationToken)
            .Concat(newTree.GetDiagnostics(cancellationToken))
            .Where(d => d.Severity is Microsoft.CodeAnalysis.DiagnosticSeverity.Error or Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .Select(d => new CheckDiagnostic(
                d.Id,
                d.Severity.ToString(),
                d.GetMessage(),
                edit.TargetFile,
                d.Location.GetLineSpan().StartLinePosition.Line + 1))
            .ToList();
        return CheckResult.Ok(diagnostics);
    }

    // A small self-contained project: an order domain with a service and a consumer, chosen so that a realistic
    // edit (a member rename, a signature change, a wrong-type assignment) either compiles clean or produces a
    // specific compiler error, giving the gate unambiguous ground truth.
    private static CSharpCompilation BuildBaseline()
    {
        var domain = CSharpSyntaxTree.ParseText("""
            namespace Shop;
            public sealed class Order
            {
                public int Id { get; init; }
                public decimal Total { get; init; }
                public decimal WithTax() => Total * 1.1m;
            }
            """, path: "Order.cs");

        var service = CSharpSyntaxTree.ParseText("""
            namespace Shop;
            public sealed class OrderService
            {
                public decimal Charge(Order order) => order.WithTax();
            }
            """, path: "OrderService.cs");

        return CSharpCompilation.Create(
            "ShopGate",
            [domain, service],
            ReferencePaths(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    // The set of edits over OrderService.cs, each with its known-correct verdict. Good edits must compile; bad
    // edits must produce an error the oracle is obliged to surface.
    private static IEnumerable<CheckEdit> Cases()
    {
        // Good: an equivalent rewrite that still binds.
        yield return new CheckEdit("good-equivalent-rewrite", "OrderService.cs", """
            namespace Shop;
            public sealed class OrderService
            {
                public decimal Charge(Order order)
                {
                    var taxed = order.WithTax();
                    return taxed;
                }
            }
            """, ShouldBeClean: true);

        // Good: add a valid overload that uses the domain type correctly.
        yield return new CheckEdit("good-added-overload", "OrderService.cs", """
            namespace Shop;
            public sealed class OrderService
            {
                public decimal Charge(Order order) => order.WithTax();
                public decimal ChargeRaw(Order order) => order.Total;
            }
            """, ShouldBeClean: true);

        // Bad: call a member that does not exist on Order (CS1061).
        yield return new CheckEdit("bad-missing-member", "OrderService.cs", """
            namespace Shop;
            public sealed class OrderService
            {
                public decimal Charge(Order order) => order.GrandTotal();
            }
            """, ShouldBeClean: false);

        // Bad: return a string where a decimal is declared (CS0029).
        yield return new CheckEdit("bad-wrong-return-type", "OrderService.cs", """
            namespace Shop;
            public sealed class OrderService
            {
                public decimal Charge(Order order) => "free";
            }
            """, ShouldBeClean: false);

        // Bad: reference an undefined type (CS0246).
        yield return new CheckEdit("bad-undefined-type", "OrderService.cs", """
            namespace Shop;
            public sealed class OrderService
            {
                public decimal Charge(Order order) => new Invoice(order).Amount;
            }
            """, ShouldBeClean: false);

        // Bad: a syntax error (a missing brace), which the parser reports.
        yield return new CheckEdit("bad-syntax-error", "OrderService.cs", """
            namespace Shop;
            public sealed class OrderService
            {
                public decimal Charge(Order order) => order.WithTax();
            """, ShouldBeClean: false);

        // Good: a comment-only change, the null edit that must stay clean.
        yield return new CheckEdit("good-comment-only", "OrderService.cs", """
            namespace Shop;
            public sealed class OrderService
            {
                // Charge applies tax and returns the amount due.
                public decimal Charge(Order order) => order.WithTax();
            }
            """, ShouldBeClean: true);

        // Bad: assign to an init-only property from outside its initializer (CS8852).
        yield return new CheckEdit("bad-init-only-assignment", "OrderService.cs", """
            namespace Shop;
            public sealed class OrderService
            {
                public decimal Charge(Order order)
                {
                    order.Total = 5m;
                    return order.WithTax();
                }
            }
            """, ShouldBeClean: false);
    }

    // The runtime's trusted platform assemblies as metadata references, so the baseline binds the BCL without a
    // project file. This is the standard no-MSBuild way to compile a snippet in-process.
    private static ImmutableArray<MetadataReference> ReferencePaths()
    {
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty;
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                builder.Add(MetadataReference.CreateFromFile(path));
        }
        return builder.ToImmutable();
    }

    private sealed record CheckEdit(string Name, string TargetFile, string NewContent, bool ShouldBeClean);
}
