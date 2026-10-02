using System.Xml.Linq;
using Fuse.Dotnet;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

public sealed class RunSettingsFileTests : IDisposable
{
    private readonly string _directory = FixtureRepo.NewDirectory();

    [Fact]
    public void Without_project_settings_the_file_holds_only_the_filter()
    {
        var path = RunSettingsFile.Write(Path.Combine(_directory, "results"), "FullyQualifiedName~Ns.C.M", null);

        Assert.Equal(Path.Combine(_directory, "results", "fuse.runsettings"), path);
        var root = XDocument.Load(path).Root!;
        Assert.Equal("RunSettings", root.Name.LocalName);
        Assert.Equal("FullyQualifiedName~Ns.C.M", root.Element("RunConfiguration")!.Element("TestCaseFilter")!.Value);
    }

    [Fact]
    public void The_project_settings_are_kept_with_the_filter_added()
    {
        var project = Write("settings/test.runsettings", """
            <?xml version="1.0" encoding="utf-8"?>
            <RunSettings>
              <RunConfiguration>
                <MaxCpuCount>1</MaxCpuCount>
                <EnvironmentVariables><PROBE>yes</PROBE></EnvironmentVariables>
              </RunConfiguration>
              <xUnit><ParallelizeTestCollections>false</ParallelizeTestCollections></xUnit>
            </RunSettings>
            """);

        var root = XDocument.Load(RunSettingsFile.Write(Path.Combine(_directory, "results"), "FullyQualifiedName~Ns.C.M", project)).Root!;

        var configuration = root.Element("RunConfiguration")!;
        Assert.Equal("1", configuration.Element("MaxCpuCount")!.Value);
        Assert.Equal("yes", configuration.Element("EnvironmentVariables")!.Element("PROBE")!.Value);
        Assert.Equal("FullyQualifiedName~Ns.C.M", configuration.Element("TestCaseFilter")!.Value);
        Assert.Equal("false", root.Element("xUnit")!.Element("ParallelizeTestCollections")!.Value);
    }

    [Fact]
    public void A_filter_the_project_settings_already_have_keeps_applying()
    {
        var project = Write("test.runsettings", "<RunSettings><RunConfiguration><TestCaseFilter>Category!=Slow</TestCaseFilter></RunConfiguration></RunSettings>");

        var root = XDocument.Load(RunSettingsFile.Write(_directory, "FullyQualifiedName~Ns.C.M", project)).Root!;

        Assert.Equal("(Category!=Slow)&(FullyQualifiedName~Ns.C.M)", root.Element("RunConfiguration")!.Element("TestCaseFilter")!.Value);
    }

    [Fact]
    public void Relative_paths_in_the_project_settings_are_made_absolute_against_their_folder()
    {
        var project = Write("settings/test.runsettings", """
            <RunSettings>
              <RunConfiguration>
                <ResultsDirectory>out</ResultsDirectory>
                <TestAdaptersPaths>adapters;%HOME%/more</TestAdaptersPaths>
              </RunConfiguration>
              <MSTest><SettingsFile>legacy.testsettings</SettingsFile></MSTest>
            </RunSettings>
            """);

        var root = XDocument.Load(RunSettingsFile.Write(Path.Combine(_directory, "results"), null, project)).Root!;

        var folder = Path.Combine(_directory, "settings");
        var configuration = root.Element("RunConfiguration")!;
        Assert.Equal(Path.Combine(folder, "out"), configuration.Element("ResultsDirectory")!.Value);
        Assert.Equal($"{Path.Combine(folder, "adapters")};%HOME%/more", configuration.Element("TestAdaptersPaths")!.Value);
        Assert.Equal(Path.Combine(folder, "legacy.testsettings"), root.Element("MSTest")!.Element("SettingsFile")!.Value);
        Assert.Null(configuration.Element("TestCaseFilter"));
    }

    [Fact]
    public void Project_settings_that_are_missing_or_not_xml_leave_the_filter_only()
    {
        var broken = Write("broken.runsettings", "<RunSettings>");

        foreach (var project in new[] { broken, Path.Combine(_directory, "missing.runsettings") })
        {
            var root = XDocument.Load(RunSettingsFile.Write(Path.Combine(_directory, "results"), "FullyQualifiedName~Ns.C.M", project)).Root!;
            Assert.Equal("<RunSettings><RunConfiguration><TestCaseFilter>FullyQualifiedName~Ns.C.M</TestCaseFilter></RunConfiguration></RunSettings>", root.ToString(SaveOptions.DisableFormatting));
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
