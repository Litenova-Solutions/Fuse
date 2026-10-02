using System.Xml;
using System.Xml.Linq;

namespace Fuse.Dotnet;

/// <summary>
///     The runsettings file one VSTest run of <c>dotnet test</c> receives with <c>--settings</c>. It carries the test
///     filter as <c>RunConfiguration/TestCaseFilter</c>, so the filter's length does not count against the command line,
///     and it is the project's own runsettings file with that filter added, so a run with it keeps the project's settings.
/// </summary>
internal static class RunSettingsFile
{
    private const string FileName = "fuse.runsettings";

    /// <summary>
    ///     Writes the runsettings file into <paramref name="directory"/> and returns its path.
    /// </summary>
    /// <param name="directory">The run's results folder in the state directory, which is deleted after the run.</param>
    /// <param name="filter">A VSTest filter expression, or null to add none.</param>
    /// <param name="projectSettings">
    ///     The absolute path of the project's own runsettings file, or null. A filter it already has keeps applying: the
    ///     written filter is both. A file that is missing or not XML is left out, and the written file has the filter only.
    /// </param>
    public static string Write(string directory, string? filter, string? projectSettings)
    {
        var document = Read(projectSettings) ?? new XDocument(new XElement("RunSettings"));
        var configuration = document.Root!.Element("RunConfiguration");
        if (configuration is null)
        {
            configuration = new XElement("RunConfiguration");
            document.Root.AddFirst(configuration);
        }

        if (projectSettings is not null)
            MakePathsAbsolute(document.Root, Path.GetDirectoryName(projectSettings)!);
        if (filter is not null)
        {
            var existing = configuration.Element("TestCaseFilter");
            var own = existing?.Value.Trim() ?? "";
            var value = own.Length == 0 ? filter : $"({own})&({filter})";
            if (existing is null)
                configuration.Add(new XElement("TestCaseFilter", value));
            else
                existing.Value = value;
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        document.Save(path);
        return path;
    }

    private static XDocument? Read(string? path)
    {
        if (path is null || !File.Exists(path))
            return null;
        try
        {
            var document = XDocument.Load(path);
            return document.Root?.Name.LocalName == "RunSettings" ? document : null;
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     VSTest reads the relative paths in a runsettings file from the file's own folder. The written file lives in the
    ///     state directory, so the relative paths it copies are made absolute against the project's file's folder.
    /// </summary>
    private static void MakePathsAbsolute(XElement root, string folder)
    {
        if (root.Element("RunConfiguration") is { } configuration)
        {
            Absolute(configuration.Element("ResultsDirectory"), folder);
            if (configuration.Element("TestAdaptersPaths") is { } adapters)
                adapters.Value = string.Join(';', adapters.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => AbsolutePath(p, folder)));
        }

        Absolute(root.Element("MSTest")?.Element("SettingsFile"), folder);
    }

    private static void Absolute(XElement? element, string folder)
    {
        if (element is not null && element.Value.Trim().Length > 0)
            element.Value = AbsolutePath(element.Value.Trim(), folder);
    }

    // A path with an environment variable in it is left as written, because VSTest expands the variable first.
    private static string AbsolutePath(string path, string folder) =>
        Path.IsPathRooted(path) || path.Contains('%', StringComparison.Ordinal) ? path : Path.GetFullPath(Path.Combine(folder, path));
}
