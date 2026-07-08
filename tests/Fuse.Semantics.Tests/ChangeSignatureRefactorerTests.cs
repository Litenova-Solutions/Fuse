using System.Runtime.CompilerServices;
using Fuse.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Fuse.Semantics.Tests;

// T3: constrained, verify-gated change-signature. The rewrite + verify-gate core is tested deterministically over
// an in-memory AdhocWorkspace (no MSBuild), so it runs identically in every environment; a tolerant integration
// test covers the MSBuild-loaded path over the SampleShop fixture.
public sealed class ChangeSignatureRefactorerTests
{
    // A minimal in-memory C# solution with the given sources, referencing the runtime so it compiles clean.
    private static Solution SolutionWith(params string[] sources)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
        };
        var projectInfo = ProjectInfo.Create(
            projectId, VersionStamp.Default, "Fixture", "Fixture", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: references);
        var solution = workspace.CurrentSolution.AddProject(projectInfo);

        for (var i = 0; i < sources.Length; i++)
        {
            var docId = DocumentId.CreateNewId(projectId);
            solution = solution.AddDocument(docId, $"Source{i}.cs", SourceText.From(sources[i]), filePath: $"Source{i}.cs");
        }

        return solution;
    }

    [Fact]
    public async Task Empty_inputs_abstain_without_loading()
    {
        var result = await new ChangeSignatureRefactorer().AddParameterAsync(
            "nonexistent.sln", methodName: "", containingTypeName: null, parameterType: "int", parameterName: "n",
            argumentValue: "0", CancellationToken.None);
        Assert.False(result.Changed);
        Assert.False(string.IsNullOrEmpty(result.Reason));
    }

    [Fact]
    public async Task Add_parameter_stages_a_diff_that_touches_declaration_and_call_site()
    {
        var solution = SolutionWith("""
            namespace Fix;
            public class Calc
            {
                public int Bar(int x) => x + 1;
            }
            public class Caller
            {
                public int Use() => new Calc().Bar(41);
            }
            """);

        var result = await new ChangeSignatureRefactorer().AddParameterToSolutionAsync(
            solution, "Bar", containingTypeName: "Calc", parameterType: "int", parameterName: "n",
            argumentValue: "0", CancellationToken.None);

        Assert.True(result.Changed, result.Reason);
        Assert.NotEmpty(result.Diffs);
        var text = string.Join("\n", result.Diffs.Select(d => d.UnifiedDiff));
        Assert.Contains("int n", text);   // the declaration gained the parameter
        Assert.Contains("Bar(41, 0)", text); // the call site gained the argument
    }

    [Fact]
    public async Task Add_parameter_propagates_across_an_interface_and_its_implementation()
    {
        var solution = SolutionWith("""
            namespace Fix;
            public interface ICalc { int Bar(int x); }
            public class Calc : ICalc { public int Bar(int x) => x + 1; }
            public class Caller
            {
                public int Use(ICalc c) => c.Bar(41);
            }
            """);

        var result = await new ChangeSignatureRefactorer().AddParameterToSolutionAsync(
            solution, "Bar", containingTypeName: "Calc", parameterType: "int", parameterName: "n",
            argumentValue: "0", CancellationToken.None);

        Assert.True(result.Changed, result.Reason);
        // Both the interface declaration and the implementation must gain the parameter, or the change would not
        // verify clean (the implementation would no longer satisfy the interface).
        var text = string.Join("\n", result.Diffs.Select(d => d.UnifiedDiff));
        Assert.Contains("int Bar(int x, int n)", text);
        Assert.Contains("int n", text);
    }

    [Fact]
    public async Task Abstains_when_the_rewrite_would_not_compile()
    {
        var solution = SolutionWith("""
            namespace Fix;
            public class Calc
            {
                public int Bar(int x) => x + 1;
            }
            """);

        // A parameter of a type that does not exist makes the rewritten declaration fail to compile; the verify
        // gate must catch it and abstain, naming the diagnostic - never return the broken diff.
        var result = await new ChangeSignatureRefactorer().AddParameterToSolutionAsync(
            solution, "Bar", containingTypeName: "Calc", parameterType: "NoSuchType", parameterName: "n",
            argumentValue: "default", CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Contains("new compile error", result.Reason);
    }

    [Fact]
    public async Task Ambiguous_method_abstains()
    {
        var solution = SolutionWith("""
            namespace Fix;
            public class A { public void Bar() { } }
            public class B { public void Bar() { } }
            """);

        var result = await new ChangeSignatureRefactorer().AddParameterToSolutionAsync(
            solution, "Bar", containingTypeName: null, parameterType: "int", parameterName: "n",
            argumentValue: "0", CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Contains("ambiguous", result.Reason);
    }

    [Fact]
    public async Task Params_method_abstains()
    {
        var solution = SolutionWith("""
            namespace Fix;
            public class Calc { public int Sum(params int[] xs) => 0; }
            """);

        var result = await new ChangeSignatureRefactorer().AddParameterToSolutionAsync(
            solution, "Sum", containingTypeName: "Calc", parameterType: "int", parameterName: "n",
            argumentValue: "0", CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Contains("params", result.Reason);
    }

    [Fact]
    public async Task Missing_method_abstains()
    {
        var solution = SolutionWith("namespace Fix; public class Calc { public int Bar(int x) => x; }");
        var result = await new ChangeSignatureRefactorer().AddParameterToSolutionAsync(
            solution, "DoesNotExist", containingTypeName: null, parameterType: "int", parameterName: "n",
            argumentValue: "0", CancellationToken.None);
        Assert.False(result.Changed);
        Assert.Contains("not found", result.Reason);
    }

    [Fact]
    public async Task Thread_cancellation_token_threads_an_in_scope_token_and_flags_a_token_less_site()
    {
        var solution = SolutionWith("""
            using System.Threading;
            namespace Fix;
            public class Service
            {
                public int Work(int x) => x;
            }
            public class HasToken
            {
                public int Call(CancellationToken ct) => new Service().Work(1);
            }
            public class NoToken
            {
                public int Call() => new Service().Work(2);
            }
            """);

        var result = await new ChangeSignatureRefactorer().ThreadCancellationTokenInSolutionAsync(
            solution, "Work", containingTypeName: "Service", parameterName: "cancellationToken", CancellationToken.None);

        Assert.True(result.Changed, result.Reason);
        var text = string.Join("\n", result.Diffs.Select(d => d.UnifiedDiff));
        // The declaration gains the token; the caller that has one threads it; the caller without one gets default.
        Assert.Contains("CancellationToken cancellationToken", text);
        Assert.Contains("Work(1, ct)", text);
        Assert.Contains("Work(2, default)", text);
        // The token-less call site is surfaced as a manual follow-up, not silently defaulted without notice.
        Assert.Single(result.ManualFollowUps);
    }

    [Fact]
    public async Task Integration_over_the_fixture_stages_or_abstains_cleanly()
    {
        var sln = SampleShopSolution();
        if (sln is null)
            return; // Fixture not present.

        // Tolerant of an environment where the solution does not load: the contract is a staged diff or a clean
        // abstention with a reason, never a throw or a partial change.
        var result = await new ChangeSignatureRefactorer().AddParameterAsync(
            sln, methodName: "DoesNotExistAnywhere", containingTypeName: null, parameterType: "int",
            parameterName: "n", argumentValue: "0", CancellationToken.None);

        Assert.False(result.Changed);
        Assert.False(string.IsNullOrEmpty(result.Reason));
    }

    private static string? SampleShopSolution([CallerFilePath] string sourceFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fuse.slnx")))
            dir = dir.Parent;
        if (dir is null)
            return null;
        var sln = Path.Combine(dir.FullName, "tests", "fixtures", "SampleShop", "SampleShop.sln");
        return File.Exists(sln) ? sln : null;
    }
}
