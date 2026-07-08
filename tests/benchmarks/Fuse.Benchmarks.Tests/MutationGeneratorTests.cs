using System.Collections.Immutable;
using Fuse.Benchmarks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Fuse.Benchmarks.Tests;

// H1: the mutation generator's contract is that its verdicts are compiler-verified, not asserted. Every breaking
// case it returns must actually fail to compile with an error in the edited file, and every neutral case must
// actually compile clean. These tests re-check that guarantee against a fresh compiler pass, and confirm the
// generator draws from several operators so the gate exercises more than one edit shape.
public sealed class MutationGeneratorTests
{
    [Fact]
    public void Every_breaking_case_fails_compilation_in_the_edited_file()
    {
        var (compilation, files) = BuildBaseline();
        var cases = new MutationGenerator().Generate(compilation, files, perClass: 20, seed: 123);

        var breaking = cases.Where(c => !c.ShouldBeClean).ToList();
        Assert.NotEmpty(breaking);
        foreach (var mutant in breaking)
        {
            var errorsInFile = RecompileErrors(compilation, mutant)
                .Count(d => d.Location.SourceTree?.FilePath == mutant.TargetFile);
            Assert.True(errorsInFile > 0, $"breaking case {mutant.Name} did not fail in {mutant.TargetFile}");
        }
    }

    [Fact]
    public void Every_neutral_case_compiles_clean()
    {
        var (compilation, files) = BuildBaseline();
        var cases = new MutationGenerator().Generate(compilation, files, perClass: 20, seed: 123);

        var neutral = cases.Where(c => c.ShouldBeClean).ToList();
        Assert.NotEmpty(neutral);
        foreach (var mutant in neutral)
        {
            var errors = RecompileErrors(compilation, mutant).ToList();
            Assert.True(errors.Count == 0, $"neutral case {mutant.Name} introduced {errors.Count} error(s)");
        }
    }

    [Fact]
    public void Generation_is_deterministic_for_a_seed()
    {
        var (compilation, files) = BuildBaseline();
        var a = new MutationGenerator().Generate(compilation, files, perClass: 10, seed: 42);
        var b = new MutationGenerator().Generate(compilation, files, perClass: 10, seed: 42);

        Assert.Equal(a.Select(c => c.NewContent), b.Select(c => c.NewContent));
    }

    [Fact]
    public void Draws_from_multiple_operators_in_each_class()
    {
        var (compilation, files) = BuildBaseline();
        var cases = new MutationGenerator().Generate(compilation, files, perClass: 30, seed: 7);

        var breakingOps = cases.Where(c => !c.ShouldBeClean).Select(c => c.OperatorId).Distinct().Count();
        var neutralOps = cases.Where(c => c.ShouldBeClean).Select(c => c.OperatorId).Distinct().Count();
        Assert.True(breakingOps >= 2, $"expected >=2 breaking operators, saw {breakingOps}");
        Assert.True(neutralOps >= 2, $"expected >=2 neutral operators, saw {neutralOps}");
    }

    [Fact]
    public void Behavior_mutants_compile_clean_and_flip_a_condition_or_comparison()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            namespace Shop;
            public sealed class Calc
            {
                public int Clamp(int x)
                {
                    if (x > 10) { return 10; }
                    return x;
                }
                public bool IsPositive(int x) => x > 0;
            }
            """, path: "Calc.cs");
        var compilation = CSharpCompilation.Create(
            "BehaviorBaseline", [tree], ReferencePaths(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var mutants = new MutationGenerator().GenerateBehaviorMutants(compilation, ["Calc.cs"], count: 6, seed: 99);

        Assert.NotEmpty(mutants);
        foreach (var mutant in mutants)
        {
            // A behavior mutant must still compile clean (it changes runtime behavior, not the types)...
            var errors = RecompileErrors(compilation, mutant).ToList();
            Assert.True(errors.Count == 0, $"behavior mutant {mutant.Name} introduced {errors.Count} error(s)");
            // ...and it must actually change the source.
            Assert.NotEqual(tree.ToString(), mutant.NewContent);
        }

        Assert.Contains(mutants, m => m.OperatorId is "negate-condition" or "flip-relational");
    }

    private static IEnumerable<Diagnostic> RecompileErrors(CSharpCompilation baseline, MutationCase mutant)
    {
        var oldTree = baseline.SyntaxTrees.First(t => t.FilePath == mutant.TargetFile);
        var newTree = CSharpSyntaxTree.ParseText(mutant.NewContent, path: mutant.TargetFile);
        return baseline.ReplaceSyntaxTree(oldTree, newTree)
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error);
    }

    // A small clean compilation with the surface the operators target: member accesses, returns, private members,
    // typed locals, and several members per type (for reordering).
    private static (CSharpCompilation Compilation, IReadOnlyCollection<string> Files) BuildBaseline()
    {
        var order = CSharpSyntaxTree.ParseText("""
            namespace Shop;
            public sealed class Order
            {
                public int Id { get; init; }
                public decimal Total { get; init; }
                public decimal WithTax() => Total * 1.1m;
                private decimal Discount() => Total * 0.1m;
                public decimal NetTotal()
                {
                    decimal d = Discount();
                    return Total - d;
                }
            }
            """, path: "Order.cs");

        var service = CSharpSyntaxTree.ParseText("""
            namespace Shop;
            public sealed class OrderService
            {
                public decimal Charge(Order order)
                {
                    Order local = order;
                    return local.WithTax();
                }
                public decimal Raw(Order order) => order.Total;
            }
            """, path: "OrderService.cs");

        var compilation = CSharpCompilation.Create(
            "MutationBaseline",
            [order, service],
            ReferencePaths(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return (compilation, ["Order.cs", "OrderService.cs"]);
    }

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
}
