using Fuse.Graph;
using Fuse.Paths;
using Fuse.Testing;
using Fuse.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Fuse.Tests.Unit;

/// <summary>
///     Which members an application host or a framework calls. A member wrongly left out has its tests missed when no
///     source calls it; a member wrongly included runs an application's test projects whole for a change only a library
///     test reaches.
/// </summary>
public class HostRuleTests
{
    private const string Source = """
        using System;
        using System.Diagnostics;

        public interface ILocal
        {
            void Run();
        }

        public class Plain : ILocal
        {
            public void Helper() { }
            public void Run() { }
            [Obsolete] public void Old() { }
            [DebuggerStepThrough] public void Stepped() { }
            [STAThread] public void Attributed() { }
            [STAThread] private void Hidden() { }
            public override string ToString() => "plain";
        }

        public class Money : IComparable<Money>
        {
            public int CompareTo(Money other) => 0;
        }

        public class Failure : Exception
        {
            public void Describe() { }
        }

        public static class Program
        {
            static void Main() { }
        }
        """;

    private static readonly Lazy<CSharpCompilation> Library = new(() => Compile(Source, OutputKind.DynamicallyLinkedLibrary));

    [Fact]
    public void An_entry_point_is_Main_or_the_method_for_top_level_statements()
    {
        var topLevel = Compile("var answer = 42;", OutputKind.ConsoleApplication).GetEntryPoint(TestContext.Current.CancellationToken);

        Assert.True(HostRule.IsEntryPoint((IMethodSymbol)Member("Program", "Main")));
        Assert.True(HostRule.IsEntryPoint(Assert.IsAssignableFrom<IMethodSymbol>(topLevel)));
        Assert.False(HostRule.IsEntryPoint((IMethodSymbol)Member("Plain", "Helper")));
    }

    [Fact]
    public void A_member_only_source_calls_is_not_framework_invoked()
    {
        // Run implements an interface declared in the repository, so the walk follows its callers through that interface.
        foreach (var name in new[] { "Helper", "Run", "Old", "Stepped" })
            Assert.False(HostRule.IsFrameworkInvoked(Member("Plain", name)), name);
    }

    [Fact]
    public void A_member_a_framework_can_call_is_framework_invoked()
    {
        Assert.True(HostRule.IsFrameworkInvoked(Member("Plain", "ToString")), "overrides a member declared outside the repository");
        Assert.True(HostRule.IsFrameworkInvoked(Member("Money", "CompareTo")), "implements an interface declared outside the repository");
        Assert.True(HostRule.IsFrameworkInvoked(Member("Failure", "Describe")), "its type derives from an external base class");
        Assert.True(HostRule.IsFrameworkInvoked(Member("Plain", "Attributed")), "carries an attribute a framework reads");
    }

    [Fact]
    public void A_private_member_is_framework_invoked_only_as_an_entry_point()
    {
        Assert.False(HostRule.IsFrameworkInvoked(Member("Plain", "Hidden")));
        Assert.True(HostRule.IsFrameworkInvoked(Member("Program", "Main")));
    }

    [Fact]
    public void Framework_invoked_code_is_called_by_the_host_only_in_an_application()
    {
        var toString = Member("Plain", "ToString");

        Assert.True(HostRule.IsCalledByHost(toString, Project(isExecutable: true)));
        Assert.False(HostRule.IsCalledByHost(toString, Project(isExecutable: false)));
        Assert.True(HostRule.IsCalledByHost(Member("Program", "Main"), Project(isExecutable: false)));
        Assert.False(HostRule.IsCalledByHost(Member("Plain", "Helper"), Project(isExecutable: true)));
    }

    private static ISymbol Member(string type, string name) =>
        Library.Value.GetTypeByMetadataName(type)!.GetMembers(name).Single();

    private static CSharpCompilation Compile(string source, OutputKind kind)
    {
        var compilation = CSharpCompilation.Create(
            "HostRuleFixture",
            [CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(kind));
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error));
        return compilation;
    }

    /// <summary>An application or a library project; its paths are never read, so any root will do.</summary>
    private static ProjectNode Project(bool isExecutable) => new()
    {
        Path = FixtureRepo.CheckoutRoot.PathOf("App/App.csproj"),
        Name = "App",
        Directory = FixtureRepo.CheckoutRoot.PathOf("App"),
        References = [],
        Sources = new HashSet<RepoPath>(),
        EvaluationInputs = new HashSet<RepoPath>(),
        AssetsFile = FixtureRepo.CheckoutRoot.PathOf("App/obj/project.assets.json"),
        IsTest = false,
        IsExecutable = isExecutable,
        UsesTestingPlatform = false,
    };
}
