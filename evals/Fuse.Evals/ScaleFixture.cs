using Fuse.Dotnet;

namespace Fuse.Evals;

/// <summary>
///     Creates the repository the scale suite measures: class libraries of nearly identical, self-contained classes, and
///     one test project referencing them, committed to git and built. A body edit in one class binds no other file, and
///     nothing needs a package beyond the SDK, so generating and building stays minutes.
/// </summary>
internal static class ScaleFixture
{
    /// <summary>How many class libraries the recorded fixture holds.</summary>
    public const int DefaultProjects = 30;

    /// <summary>How many class files each library holds.</summary>
    public const int DefaultFiles = 200;

    /// <summary>How many files the recorded run changes.</summary>
    public const int DefaultChanged = 1500;

    public static async Task<string> CreateAsync(string directory, int projects = DefaultProjects, int files = DefaultFiles, int changed = DefaultChanged)
    {
        var wanted = $"{projects}\n{files}\n{changed}\n";
        var paramsPath = Path.Combine(directory, ".scale-params");
        if (Directory.Exists(Path.Combine(directory, ".git")) && File.Exists(Path.Combine(directory, "ScaleFixture.sln"))
            && File.Exists(paramsPath) && await File.ReadAllTextAsync(paramsPath) == wanted)
            return directory;
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);

        async Task Run(string file, params string[] args)
        {
            var result = await ProcessRunner.RunAsync(file, args, directory, CancellationToken.None);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"{file} {string.Join(' ', args)} failed:\n{result.Output}");
        }

        // The fixture lives inside the Fuse repository; these files stop MSBuild from importing Fuse's own build settings.
        await File.WriteAllTextAsync(Path.Combine(directory, "global.json"), """{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }""");
        await File.WriteAllTextAsync(Path.Combine(directory, "Directory.Build.props"), "<Project />");
        await File.WriteAllTextAsync(Path.Combine(directory, "Directory.Build.targets"), "<Project />");
        await File.WriteAllTextAsync(
            Path.Combine(directory, "Directory.Packages.props"),
            "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        var projectPaths = new List<string>();
        for (var p = 0; p < projects; p++)
        {
            var name = $"Gen{p:00}";
            Directory.CreateDirectory(Path.Combine(directory, "src", name));
            await File.WriteAllTextAsync(Path.Combine(directory, "src", name, $"{name}.csproj"), LibraryProject);
            for (var i = 0; i < files; i++)
                await File.WriteAllTextAsync(Path.Combine(directory, PartPath(p, i)), Part(p, i));
            projectPaths.Add(Path.Combine("src", name, $"{name}.csproj"));
        }

        Directory.CreateDirectory(Path.Combine(directory, "tests", "Scale.Tests"));
        var references = string.Join("\n", Enumerable.Range(0, projects).Select(p => $"    <ProjectReference Include=\"../../src/Gen{p:00}/Gen{p:00}.csproj\" />"));
        await File.WriteAllTextAsync(Path.Combine(directory, "tests", "Scale.Tests", "Scale.Tests.csproj"), TestProject(references));
        await File.WriteAllTextAsync(Path.Combine(directory, "tests", "Scale.Tests", "Probe00.cs"), "namespace Scale.Tests;\n\npublic static class Probe00\n{\n    public static int Probe() => Gen00.Class000.Twice(21);\n}\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "tests", "Scale.Tests", "Probe01.cs"), $"namespace Scale.Tests;\n\npublic static class Probe01\n{{\n    public static int Probe() => Gen{projects - 1:00}.Class000.Twice(21);\n}}\n");
        projectPaths.Add(Path.Combine("tests", "Scale.Tests", "Scale.Tests.csproj"));
        await File.WriteAllTextAsync(Path.Combine(directory, ".gitignore"), "bin/\nobj/\n");
        await File.WriteAllTextAsync(paramsPath, wanted);

        await Run("git", "init", "-q");
        await Run("dotnet", "new", "sln", "-n", "ScaleFixture", "--format", "sln");
        await Run("dotnet", ["sln", "ScaleFixture.sln", "add", .. projectPaths]);
        await Run("git", "add", "-A");
        await Run("git", "-c", "user.email=evals@fuse.local", "-c", "user.name=fuse evals", "commit", "-qm", "scale fixture");
        await Run("dotnet", "build", "ScaleFixture.sln", "-nologo", "-v:q");
        return directory;
    }

    /// <summary>The fixture size the scale options name: <c>--projects</c>, <c>--files</c> and <c>--changed</c>, or the recorded size.</summary>
    public static (int Projects, int Files, int Changed) Args(Dictionary<string, string> options)
    {
        var projects = Option(options, "projects", DefaultProjects);
        var files = Option(options, "files", DefaultFiles);
        var changed = Option(options, "changed", DefaultChanged);
        if (projects <= 0 || files <= 0 || changed <= 0 || changed > projects * files)
            throw new ArgumentException($"--projects {projects} --files {files} --changed {changed} names no fixture: each must be positive and changed must fit in projects times files");
        return (projects, files, changed);
    }

    /// <summary>The class files to change, spread evenly across the projects: file 0 of each project first, then file 1, and so on.</summary>
    public static List<(int Project, int File)> ChangedFiles(int projects, int files, int changed)
    {
        var result = new List<(int Project, int File)>();
        for (var file = 0; file < files && result.Count < changed; file++)
        {
            for (var project = 0; project < projects && result.Count < changed; project++)
                result.Add((project, file));
        }

        return result;
    }

    /// <summary>The path of a class file, relative to the fixture.</summary>
    public static string PartPath(int project, int file) => Path.Combine("src", $"Gen{project:00}", $"Class{file:000}.cs");

    /// <summary>
    ///     Class <paramref name="file"/> of project <paramref name="project"/>: two methods calling nothing outside the
    ///     file, with <paramref name="value"/> in one method body so a rewrite with another value is a body edit.
    /// </summary>
    public static string Part(int project, int file, int value = 0) =>
        $"namespace Gen{project:00};\n\npublic static class Class{file:000}\n{{\n    public static int Sum(int n)\n    {{\n        var total = {value};\n        for (var k = 0; k < n; k++)\n            total += Twice(k);\n        return total;\n    }}\n\n    public static int Twice(int k) => k < 0 ? 0 : k * 2;\n}}\n";

    private static int Option(Dictionary<string, string> options, string name, int fallback) =>
        options.TryGetValue(name, out var text) ? int.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : fallback;

    private const string LibraryProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private static string TestProject(string references) =>
        $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
          <ItemGroup>
        {{references}}
          </ItemGroup>
        </Project>
        """;
}
