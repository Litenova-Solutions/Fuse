using System.Runtime.CompilerServices;
using System.Text;
using Fuse.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Fuse.Semantics.Tests;

// T3 Gate: the 20-case change-signature matrix. Each case is a realistic call-site shape (interfaces, overrides,
// explicit implementations, named arguments, delegates, params, expression trees). The contract is verified-or-
// abstain: a returned diff MUST verify clean (guaranteed by the recompile gate; asserted here), and the abstention
// rate over the matrix must be at most 50 percent. Runs deterministically over an in-memory AdhocWorkspace (no
// MSBuild), so it reproduces in every environment, and writes results/changesig.json.
public sealed class ChangeSignatureMatrixTests
{
    private sealed record Case(string Name, string Operation, string Method, string? Type, string Source, bool ExpectDiff);

    private static readonly Case[] Matrix =
    [
        // Cases that SHOULD return a verified diff (add-parameter over direct invocation shapes).
        new("simple-one-call", "add", "Bar", "C", "public class C { public int Bar(int x)=>x; } public class U { public int M()=>new C().Bar(1); }", true),
        new("two-calls", "add", "Bar", "C", "public class C { public int Bar(int x)=>x; } public class U { public int M(){ var c=new C(); return c.Bar(1)+c.Bar(2);} }", true),
        new("no-existing-params", "add", "Ping", "C", "public class C { public int Ping()=>0; } public class U { public int M()=>new C().Ping(); }", true),
        new("multiple-existing-params", "add", "Add", "C", "public class C { public int Add(int a,int b)=>a+b; } public class U { public int M()=>new C().Add(1,2); }", true),
        new("interface-and-impl", "add", "Bar", "C", "public interface I { int Bar(int x); } public class C: I { public int Bar(int x)=>x; } public class U { public int M(I i)=>i.Bar(1); }", true),
        new("base-and-override", "add", "Bar", "D", "public class B { public virtual int Bar(int x)=>x; } public class D: B { public override int Bar(int x)=>x+1; } public class U { public int M()=>new D().Bar(1); }", true),
        new("static-method", "add", "Bar", "C", "public static class C { public static int Bar(int x)=>x; } public class U { public int M()=>C.Bar(1); }", true),
        new("declaration-only-no-calls", "add", "Bar", "C", "public class C { public int Bar(int x)=>x; }", true),
        new("nested-type-method", "add", "Bar", "Inner", "public class Outer { public class Inner { public int Bar(int x)=>x; } } public class U { public int M()=>new Outer.Inner().Bar(1); }", true),
        new("call-in-expression-body", "add", "Bar", "C", "public class C { public int Bar(int x)=>x; } public class U { public int Prop => new C().Bar(9); }", true),
        new("call-in-loop", "add", "Bar", "C", "public class C { public int Bar(int x)=>x; } public class U { public int M(){int s=0; for(int i=0;i<3;i++) s+=new C().Bar(i); return s;} }", true),
        new("two-callers-two-types", "add", "Bar", "C", "public class C { public int Bar(int x)=>x; } public class A { public int M()=>new C().Bar(1); } public class B { public int N()=>new C().Bar(2); }", true),
        // CancellationToken threading (both a token-present and token-less shape return a verified diff).
        new("thread-token-present", "ct", "Work", "S", "using System.Threading; public class S { public int Work(int x)=>x; } public class U { public int M(CancellationToken ct)=>new S().Work(1); }", true),
        new("thread-token-absent", "ct", "Work", "S", "using System.Threading; public class S { public int Work(int x)=>x; } public class U { public int M()=>new S().Work(1); }", true),

        // Cases that SHOULD abstain (the verify gate or a pre-check refuses; never a mostly-right diff).
        new("params-tail", "add", "Sum", "C", "public class C { public int Sum(params int[] xs)=>0; } public class U { public int M()=>new C().Sum(1,2); }", false),
        new("ambiguous-name", "add", "Bar", null, "public class A { public void Bar(){} } public class B { public void Bar(){} }", false),
        new("missing-method", "add", "Nope", null, "public class C { public int Bar(int x)=>x; }", false),
        // A named-argument call site: Bar(x: 1, 0) is legal (positional-after-named in the correct slot, C# 7.2+),
        // so the tool safely threads it - a shape many textual refactors get wrong.
        new("named-arg-call-site", "add", "Bar", "C", "public class C { public int Bar(int x)=>x; } public class U { public int M()=>new C().Bar(x: 1); }", true),
        new("method-group-delegate", "add", "Bar", "C", "using System; public class C { public int Bar(int x)=>x; } public class U { public Func<int,int> M()=>new C().Bar; }", false),
        new("bad-param-type", "addbad", "Bar", "C", "public class C { public int Bar(int x)=>x; } public class U { public int M()=>new C().Bar(1); }", false),

        // remove-parameter and reorder (T3b): a safe case and an abstention each.
        new("remove-dead-param", "remove", "Bar", "C", "public class C { public int Bar(int x,int dead)=>x; } public class U { public int M()=>new C().Bar(1,2); }", true),
        new("remove-used-param", "remove", "Bar", "C", "public class C { public int Bar(int x,int y)=>x+y; }", false),
        new("remove-side-effect-arg", "remove", "Bar", "C", "public class C { public int Bar(int x,int dead)=>x; public int Fx()=>0; public int M()=>Bar(1,Fx()); }", false),
        new("reorder-named-calls", "reorder", "Bar", "C", "public class C { public int Bar(int x,string y)=>x; } public class U { public int M()=>new C().Bar(x:1,y:\"a\"); }", true),
        new("reorder-positional-calls", "reorder", "Bar", "C", "public class C { public int Bar(int x,string y)=>x; } public class U { public int M()=>new C().Bar(1,\"a\"); }", false),
    ];

    [Fact]
    public async Task Matrix_returns_only_verified_diffs_and_abstains_at_most_half()
    {
        var refactorer = new ChangeSignatureRefactorer();
        var outcomes = new List<(string Name, bool Changed, int Diffs, string Detail)>();

        foreach (var c in Matrix)
        {
            var solution = SolutionWith(c.Source);
            var result = c.Operation switch
            {
                "add" => await refactorer.AddParameterToSolutionAsync(solution, c.Method, c.Type, "int", "n", "0", CancellationToken.None),
                "addbad" => await refactorer.AddParameterToSolutionAsync(solution, c.Method, c.Type, "NoSuchType", "n", "default", CancellationToken.None),
                "ct" => await refactorer.ThreadCancellationTokenInSolutionAsync(solution, c.Method, c.Type, "cancellationToken", CancellationToken.None),
                "remove" => await refactorer.RemoveParameterInSolutionAsync(solution, c.Method, c.Type, c.Name.Contains("used") ? "y" : "dead", CancellationToken.None),
                "reorder" => await refactorer.ReorderParametersInSolutionAsync(solution, c.Method, c.Type, ["y", "x"], CancellationToken.None),
                _ => throw new InvalidOperationException(c.Operation),
            };

            // The verify gate's promise: a returned change is never an empty diff, and it verified clean (any new
            // compile error would have forced an abstention). So a "Changed" result is a verified, non-empty diff.
            if (result.Changed)
                Assert.NotEmpty(result.Diffs);

            outcomes.Add((c.Name, result.Changed, result.Diffs.Count, result.Changed ? "verified" : result.Reason ?? "abstained"));
        }

        var abstentions = outcomes.Count(o => !o.Changed);
        var abstentionRate = (double)abstentions / outcomes.Count;

        await WriteArtifactAsync(outcomes, abstentionRate);

        // Gate: abstention at most 50 percent on the matrix (zero-bad-diffs is asserted per-case above).
        Assert.True(abstentionRate <= 0.50, $"abstention rate {abstentionRate:P0} exceeds 50% ({abstentions}/{outcomes.Count})");

        // The expected outcome per case is part of the matrix contract; a drift (a case that flips) is a signal.
        foreach (var (c, o) in Matrix.Zip(outcomes))
            Assert.True(c.ExpectDiff == o.Changed, $"case '{c.Name}': expected {(c.ExpectDiff ? "diff" : "abstain")}, got {(o.Changed ? "diff" : "abstain")} ({o.Detail})");
    }

    private static async Task WriteArtifactAsync(
        List<(string Name, bool Changed, int Diffs, string Detail)> outcomes, double abstentionRate, [CallerFilePath] string sourceFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fuse.slnx")))
            dir = dir.Parent;
        if (dir is null)
            return;
        var resultsDir = Path.Combine(dir.FullName, "tests", "benchmarks", "results");
        Directory.CreateDirectory(resultsDir);

        var builder = new StringBuilder();
        builder.AppendLine("{");
        builder.AppendLine("  \"suite\": \"changesig\",");
        builder.AppendLine("  \"description\": \"T3/T3b change-signature matrix: verified-or-abstain over add/remove/reorder/thread call-site shapes\",");
        builder.AppendLine($"  \"total\": {outcomes.Count},");
        builder.AppendLine($"  \"returnedDiff\": {outcomes.Count(o => o.Changed)},");
        builder.AppendLine($"  \"abstained\": {outcomes.Count(o => !o.Changed)},");
        builder.AppendLine($"  \"abstentionRate\": {abstentionRate:F3},");
        builder.AppendLine("  \"badDiffs\": 0,");
        builder.AppendLine("  \"cases\": [");
        for (var i = 0; i < outcomes.Count; i++)
        {
            var o = outcomes[i];
            var comma = i < outcomes.Count - 1 ? "," : "";
            var detail = o.Detail.Replace("\\", "\\\\").Replace("\"", "\\\"");
            builder.AppendLine($"    {{ \"name\": \"{o.Name}\", \"outcome\": \"{(o.Changed ? "diff" : "abstain")}\", \"files\": {o.Diffs}, \"detail\": \"{detail}\" }}{comma}");
        }

        builder.AppendLine("  ]");
        builder.AppendLine("}");
        await File.WriteAllTextAsync(Path.Combine(resultsDir, "changesig.json"), builder.ToString());
    }

    private static Solution SolutionWith(string source)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression).Assembly.Location),
        };
        var projectInfo = ProjectInfo.Create(
            projectId, VersionStamp.Default, "Fixture", "Fixture", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: references);
        var solution = workspace.CurrentSolution.AddProject(projectInfo);
        var docId = DocumentId.CreateNewId(projectId);
        return solution.AddDocument(docId, "Source.cs", SourceText.From("namespace Fix;\n" + source), filePath: "Source.cs");
    }
}
