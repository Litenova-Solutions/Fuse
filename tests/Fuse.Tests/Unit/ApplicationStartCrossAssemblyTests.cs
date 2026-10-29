using Fuse.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Fuse.Tests.Unit;

/// <summary>
///     A test class that calls into a helper in another assembly cannot be told, so the project stays whole. A single
///     assembly cannot show this shape, because every type in it counts as a test type.
/// </summary>
public class ApplicationStartCrossAssemblyTests
{
    private const string SupportSource = """
        namespace App
        {
            public class Program
            {
            }
        }

        namespace Support
        {
            public static class Host
            {
                public static void Start()
                {
                }
            }
        }
        """;

    private const string TestSource = """
        namespace Tests
        {
            public class CallerTests
            {
                public void Calls() => Support.Host.Start();
            }

            public class DirectTests
            {
                public void Calls() => _ = 1 + 1;
            }
        }
        """;

    [Fact]
    public void Call_into_another_assembly_cannot_be_told()
    {
        var test = TestCompilation();
        Assert.Equal(
            ApplicationStart.Classification.CannotBeTold,
            ApplicationStart.Classify(
                test.GetTypeByMetadataName("Tests.CallerTests")!,
                test,
                type => type.ContainingNamespace?.ToDisplayString() == "App",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Call_into_nothing_external_does_not_start()
    {
        var test = TestCompilation();
        Assert.Equal(
            ApplicationStart.Classification.DoesNot,
            ApplicationStart.Classify(
                test.GetTypeByMetadataName("Tests.DirectTests")!,
                test,
                type => type.ContainingNamespace?.ToDisplayString() == "App",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                TestContext.Current.CancellationToken));
    }

    private static CSharpCompilation TestCompilation()
    {
        var ct = TestContext.Current.CancellationToken;
        var support = CSharpCompilation.Create(
            "SupportFixture",
            [CSharpSyntaxTree.ParseText(SupportSource, cancellationToken: ct)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(support.GetDiagnostics(ct).Where(d => d.Severity == DiagnosticSeverity.Error));
        var test = CSharpCompilation.Create(
            "CrossAssemblyFixture",
            [CSharpSyntaxTree.ParseText(TestSource, cancellationToken: ct)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location), support.ToMetadataReference()],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(test.GetDiagnostics(ct).Where(d => d.Severity == DiagnosticSeverity.Error));
        return test;
    }
}
