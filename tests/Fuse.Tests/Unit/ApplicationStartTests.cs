using System.Diagnostics;
using Fuse.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Fuse.Tests.Unit;

/// <summary>
///     Which test classes start the application. A class wrongly left out has its tests missed when the application is
///     reached through its host; a class wrongly included runs whole for a change only a direct call reaches.
/// </summary>
public class ApplicationStartTests
{
    private const string Source = """
        using System;
        using System.Diagnostics;
        using System.Reflection;
        using Microsoft.AspNetCore.Mvc.Testing;

        namespace App
        {
            public class Program
            {
            }

            public class Startup
            {
            }

            public class Base
            {
            }

            public class Report
            {
                public static string Line(int value) => "value=" + value;
            }
        }

        namespace Microsoft.AspNetCore.Mvc.Testing
        {
            public class WebApplicationFactory<TEntryPoint>
            {
            }
        }

        namespace Microsoft.AspNetCore.TestHost
        {
            public class TestServer
            {
                public TestServer(object builder)
                {
                }
            }
        }

        namespace Tests
        {
            public class Helper
            {
                public static int Twice(int value) => value * 2;
            }

            public class ConnectionFactory
            {
            }

            public class GenericFactory<T>
            {
            }

            public class TestServer
            {
                public TestServer(string name)
                {
                }
            }

            public class WebAppFixture
            {
                private readonly WebApplicationFactory<App.Program> _factory = null!;
            }

            public interface IClassFixture<T>
            {
            }

            public class Builder
            {
                public Builder UseStartup<T>() => this;
            }

            public class FactoryTests
            {
                private readonly WebApplicationFactory<App.Program> _factory = null!;
            }

            public class DerivedFactoryTests : WebApplicationFactory<App.Program>
            {
            }

            public class AppFactoryTests
            {
                private readonly GenericFactory<App.Report> _factory = null!;
            }

            public class OtherFactoryTests
            {
                private readonly ConnectionFactory _connection = null!;
                private readonly GenericFactory<string> _generic = null!;
            }

            public class ServerTests
            {
                public void Boots() => _ = new Microsoft.AspNetCore.TestHost.TestServer(typeof(App.Startup));
            }

            public class BareServerTests
            {
                public void Boots() => _ = new Microsoft.AspNetCore.TestHost.TestServer("inline");
            }

            public class HomegrownServerTests
            {
                public void Boots() => _ = new TestServer("fake");
            }

            public class ProcessTests
            {
                public void Runs() => Process.Start("App.dll");
                public void RunsHosted() => Process.Start("dotnet", "App.dll");
            }

            public class ProcessInfoTests
            {
                public void Runs() => _ = new ProcessStartInfo { FileName = "App.exe" };
            }

            public class VariableProcessTests
            {
                public void Runs(string name) => Process.Start(name);
                public void RunsHosted(string arguments) => Process.Start("dotnet", arguments);
            }

            public class NotepadProcessTests
            {
                public void Runs() => Process.Start("notepad.exe");
            }

            public class ReflectionTests
            {
                public void Loads()
                {
                    Assembly.Load("App");
                    Assembly.LoadFrom("App.dll");
                    Assembly.LoadFile("App.dll");
                }
            }

            public class GetTypeOwnTests
            {
                public void Loads() => Type.GetType("Tests.Helper");
            }

            public class GetTypeVariableTests
            {
                public void Loads(string name) => Type.GetType(name);
            }

            public class ActivatorTests
            {
                public void Creates() => Activator.CreateInstance(typeof(Tests.Helper));
            }

            public class ActivatorVariableTests
            {
                public void Creates(Type type) => Activator.CreateInstance(type);
            }

            public class GenericActivatorTests
            {
                public void Creates() => Activator.CreateInstance<Helper>();
            }

            public class ProgramTests
            {
                public void Names() => _ = typeof(App.Program);
            }

            public class AppBaseTests : App.Base
            {
            }

            public class DirectTests
            {
                public void Calls() => _ = App.Report.Line(1);
            }

            public class ExternalBaseTests : Process
            {
            }

            public class FrameworkBaseTests : Exception
            {
            }

            public class FixtureFieldTests
            {
                private readonly WebAppFixture _fixture = null!;
            }

            public class CollectionFixtureTests : IClassFixture<WebAppFixture>
            {
            }

            public class StartupTests
            {
                public void Boots() => new Builder().UseStartup<App.Startup>();
            }

            public class VarFixtureTests
            {
                public void Boots() => _ = GetFixture();

                private WebAppFixture GetFixture() => null!;
            }
        }
        """;

    private static readonly Lazy<CSharpCompilation> Fixture = new(() => Compile(Source));

    private static readonly IReadOnlySet<string> Files =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "App", "App.exe", "App.dll" };

    [Fact]
    public void Factory_of_the_application_starts_it() =>
        Assert.Equal(ApplicationStart.Classification.Starts, Classify("Tests.FactoryTests"));

    [Fact]
    public void Factory_base_with_the_application_starts_it() =>
        Assert.Equal(ApplicationStart.Classification.Starts, Classify("Tests.DerivedFactoryTests"));

    [Fact]
    public void Factory_of_an_application_type_starts_it() =>
        Assert.Equal(ApplicationStart.Classification.Starts, Classify("Tests.AppFactoryTests"));

    [Fact]
    public void Factory_without_the_application_does_not_start() =>
        Assert.Equal(ApplicationStart.Classification.DoesNot, Classify("Tests.OtherFactoryTests"));

    [Fact]
    public void Test_server_with_the_application_starts_it() =>
        Assert.Equal(ApplicationStart.Classification.Starts, Classify("Tests.ServerTests"));

    [Fact]
    public void Test_server_without_the_application_cannot_be_told() =>
        Assert.Equal(ApplicationStart.Classification.CannotBeTold, Classify("Tests.BareServerTests"));

    [Fact]
    public void Homegrown_test_server_does_not_start() =>
        Assert.Equal(ApplicationStart.Classification.DoesNot, Classify("Tests.HomegrownServerTests"));

    [Fact]
    public void Process_start_with_the_application_file_starts_it() =>
        Assert.Equal(ApplicationStart.Classification.Starts, Classify("Tests.ProcessTests"));

    [Fact]
    public void Process_start_info_with_the_application_file_starts_it() =>
        Assert.Equal(ApplicationStart.Classification.Starts, Classify("Tests.ProcessInfoTests"));

    [Fact]
    public void Process_start_with_a_variable_cannot_be_told() =>
        Assert.Equal(ApplicationStart.Classification.CannotBeTold, Classify("Tests.VariableProcessTests"));

    [Fact]
    public void Process_start_with_another_file_cannot_be_told() =>
        Assert.Equal(ApplicationStart.Classification.CannotBeTold, Classify("Tests.NotepadProcessTests"));

    [Fact]
    public void Assembly_loaded_by_name_cannot_be_told() =>
        Assert.Equal(ApplicationStart.Classification.CannotBeTold, Classify("Tests.ReflectionTests"));

    [Fact]
    public void Type_of_a_test_type_stays_classifiable() =>
        Assert.Equal(ApplicationStart.Classification.DoesNot, Classify("Tests.GetTypeOwnTests"));

    [Fact]
    public void Type_of_a_variable_cannot_be_told() =>
        Assert.Equal(ApplicationStart.Classification.CannotBeTold, Classify("Tests.GetTypeVariableTests"));

    [Fact]
    public void Activator_with_a_resolved_type_stays_classifiable() =>
        Assert.Equal(ApplicationStart.Classification.DoesNot, Classify("Tests.ActivatorTests"));

    [Fact]
    public void Activator_with_a_variable_cannot_be_told() =>
        Assert.Equal(ApplicationStart.Classification.CannotBeTold, Classify("Tests.ActivatorVariableTests"));

    [Fact]
    public void Generic_activator_with_a_test_type_stays_classifiable() =>
        Assert.Equal(ApplicationStart.Classification.DoesNot, Classify("Tests.GenericActivatorTests"));

    [Fact]
    public void Entry_type_reference_starts_it() =>
        Assert.Equal(ApplicationStart.Classification.Starts, Classify("Tests.ProgramTests"));

    [Fact]
    public void Application_base_class_starts_it() =>
        Assert.Equal(ApplicationStart.Classification.Starts, Classify("Tests.AppBaseTests"));

    [Fact]
    public void Direct_call_does_not_start() =>
        Assert.Equal(ApplicationStart.Classification.DoesNot, Classify("Tests.DirectTests"));

    [Fact]
    public void Process_base_class_does_not_start() =>
        Assert.Equal(ApplicationStart.Classification.DoesNot, Classify("Tests.ExternalBaseTests"));

    [Fact]
    public void Framework_base_class_does_not_start() =>
        Assert.Equal(ApplicationStart.Classification.DoesNot, Classify("Tests.FrameworkBaseTests"));

    [Fact]
    public void Fixture_behind_a_field_starts_it() =>
        Assert.Equal(ApplicationStart.Classification.Starts, Classify("Tests.FixtureFieldTests"));

    [Fact]
    public void Fixture_behind_an_interface_argument_starts_it() =>
        Assert.Equal(ApplicationStart.Classification.Starts, Classify("Tests.CollectionFixtureTests"));

    [Fact]
    public void Startup_type_of_the_application_starts_it() =>
        Assert.Equal(ApplicationStart.Classification.Starts, Classify("Tests.StartupTests"));

    [Fact]
    public void Fixture_behind_a_call_starts_it() =>
        Assert.Equal(ApplicationStart.Classification.Starts, Classify("Tests.VarFixtureTests"));

    private static ApplicationStart.Classification Classify(string testClass) =>
        ApplicationStart.Classify(
            Fixture.Value.GetTypeByMetadataName(testClass)!,
            Fixture.Value,
            type => type.ContainingNamespace?.ToDisplayString() == "App",
            Files,
            TestContext.Current.CancellationToken);

    private static CSharpCompilation Compile(string source)
    {
        var compilation = CSharpCompilation.Create(
            "ApplicationStartFixture",
            [CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location), MetadataReference.CreateFromFile(typeof(Process).Assembly.Location), MetadataReference.CreateFromFile(typeof(System.ComponentModel.Component).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error));
        return compilation;
    }
}
