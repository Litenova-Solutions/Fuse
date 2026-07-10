using Fuse.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Fuse.Semantics.Tests;

// T4: verify-gated type refactors. Extract-interface is tested deterministically over an in-memory AdhocWorkspace
// (no MSBuild), so it runs identically everywhere.
public sealed class TypeRefactorerTests
{
    private static Solution SolutionWith(string source)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(
            projectId, VersionStamp.Default, "Fixture", "Fixture", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var docId = DocumentId.CreateNewId(projectId);
        return workspace.CurrentSolution.AddProject(projectInfo)
            .AddDocument(docId, "Source.cs", SourceText.From(source), filePath: "Source.cs");
    }

    [Fact]
    public async Task Extract_interface_adds_the_interface_and_the_base_type_and_verifies_clean()
    {
        var solution = SolutionWith("""
            namespace Fix;
            public class Service
            {
                public int Compute(int x) => x + 1;
                public string Name { get; set; } = "";
                private int Secret() => 0;
            }
            """);

        var result = await new TypeRefactorer().ExtractInterfaceInSolutionAsync(
            solution, "Service", interfaceName: null, CancellationToken.None);

        Assert.True(result.Changed, result.Reason);
        var text = Normalize(result.Diffs.Single().NewText);
        // The interface was generated with the public members and the class now implements it.
        Assert.Contains("public interface IService", text);
        Assert.Contains("int Compute(int x)", text);
        Assert.Contains("string Name", text);
        Assert.Contains("class Service : IService", text);
        // The private member is not part of the extracted surface.
        Assert.DoesNotContain("Secret", text.Split("interface IService")[1].Split('}')[0]);
    }

    [Fact]
    public async Task Extract_interface_uses_the_given_name()
    {
        var solution = SolutionWith("""
            namespace Fix;
            public class Repo { public int Get() => 0; }
            """);

        var result = await new TypeRefactorer().ExtractInterfaceInSolutionAsync(
            solution, "Repo", interfaceName: "IReadRepo", CancellationToken.None);

        Assert.True(result.Changed, result.Reason);
        var text = Normalize(result.Diffs.Single().NewText);
        Assert.Contains("public interface IReadRepo", text);
        Assert.Contains("class Repo : IReadRepo", text);
    }

    [Fact]
    public async Task Extract_interface_abstains_with_no_public_surface()
    {
        var solution = SolutionWith("""
            namespace Fix;
            public class Empty { private int X() => 0; }
            """);

        var result = await new TypeRefactorer().ExtractInterfaceInSolutionAsync(
            solution, "Empty", interfaceName: null, CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Contains("no public instance methods or properties", result.Reason);
    }

    [Fact]
    public async Task Move_type_splits_a_multi_type_file_into_a_new_file_and_verifies_clean()
    {
        var solution = SolutionWith("""
            namespace Fix;
            public class Alpha { public int A() => 1; }
            public class Beta { public int B() => 2; }
            """);

        var result = await new TypeRefactorer().MoveTypeInSolutionAsync(solution, "Beta", CancellationToken.None);

        Assert.True(result.Changed, result.Reason);
        Assert.Equal(2, result.Diffs.Count);
        var newFile = result.Diffs.First(d => d.FilePath.EndsWith("Beta.cs"));
        var original = result.Diffs.First(d => !d.FilePath.EndsWith("Beta.cs"));
        // The new file carries Beta in its namespace; the original keeps Alpha and lost Beta.
        Assert.Contains("namespace Fix", newFile.NewText);
        Assert.Contains("class Beta", newFile.NewText);
        Assert.Contains("class Alpha", original.NewText);
        Assert.DoesNotContain("class Beta", original.NewText);
    }

    [Fact]
    public async Task Move_type_abstains_when_the_type_is_alone_in_its_file()
    {
        var solution = SolutionWith("""
            namespace Fix;
            public class Solo { public int S() => 0; }
            """);

        var result = await new TypeRefactorer().MoveTypeInSolutionAsync(solution, "Solo", CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Contains("already the only top-level type", result.Reason);
    }

    [Fact]
    public async Task Extract_interface_abstains_on_a_missing_class()
    {
        var solution = SolutionWith("namespace Fix; public class A { public int M() => 0; }");
        var result = await new TypeRefactorer().ExtractInterfaceInSolutionAsync(
            solution, "DoesNotExist", interfaceName: null, CancellationToken.None);
        Assert.False(result.Changed);
        Assert.Contains("not found", result.Reason);
    }

    // Collapse runs of whitespace so an assertion checks tokens, not exact indentation/newlines.
    private static string Normalize(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
}
