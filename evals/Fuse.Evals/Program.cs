using System.Text.Json;
using Fuse.Dotnet;

namespace Fuse.Evals;

/// <summary>
///     The v5 eval suites (docs/v5-plan.md section 7). Every fuse measurement goes through the fuse executable; the
///     truth side is a real dotnet build or dotnet test.
/// </summary>
internal static class Program
{
    private const string Usage = """
        usage: Fuse.Evals <suite> <repo> [--mutations N] [--seed S] [--solution path] [--fuse path]
          suite: correctness | selection | latency | all
          repo:  fixture (generated under evals/.work/fixture) or a path to a git repository
        """;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var suite = args[0];
        var options = Options(args.Skip(2).ToArray());
        var fuseRoot = FindFuseRoot();
        var fuse = options.GetValueOrDefault("fuse") ?? DefaultFuse(fuseRoot);
        if (!File.Exists(fuse))
        {
            Console.Error.WriteLine($"fuse executable not found at {fuse}; build Fuse.slnx -c Release first or pass --fuse");
            return 2;
        }

        var repoPath = args[1] == "fixture"
            ? await FixtureGenerator.CreateAsync(Path.Combine(fuseRoot, "evals", ".work", "fixture"))
            : Path.GetFullPath(args[1]);
        var solutionPath = options.GetValueOrDefault("solution") ?? DefaultSolution(repoPath);
        var repo = new EvalRepo(repoPath, fuse, solutionPath, []);
        var solution = SolutionInfo.Load(repoPath, solutionPath);
        Console.WriteLine($"repo {repoPath}, solution {solutionPath}: {solution.CodeProjects.Count} code project(s), {solution.TestProjects.Count} test project(s); fuse {fuse}");

        var restore = await ProcessRunner.RunAsync("dotnet", ["restore", solutionPath, "-nologo", "-v:q"], repoPath, CancellationToken.None);
        if (restore.ExitCode != 0)
        {
            Console.Error.WriteLine($"restore failed:\n{restore.Output}");
            return 1;
        }

        var seed = int.Parse(options.GetValueOrDefault("seed") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
        int Mutations(int fallback) => int.Parse(options.GetValueOrDefault("mutations") ?? fallback.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
        var suites = suite == "all" ? new[] { "correctness", "selection", "latency" } : [suite];
        foreach (var name in suites)
        {
            object result = name switch
            {
                "correctness" => await CorrectnessSuite.RunAsync(repo, solution, Mutations(30), seed),
                "selection" => await SelectionSuite.RunAsync(repo, solution, Mutations(10), seed),
                "latency" => await LatencySuite.RunAsync(repo, solution),
                _ => throw new ArgumentException($"unknown suite {name}"),
            };
            var file = Path.Combine(fuseRoot, "evals", "results", $"{name}-{repo.Name}-{DateTime.Now:yyyyMMdd-HHmm}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(result, Json));
            Console.WriteLine($"wrote {Path.GetRelativePath(fuseRoot, file)}");
        }

        return 0;
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i += 2)
            result[args[i].TrimStart('-')] = args[i + 1];
        return result;
    }

    private static string FindFuseRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Fuse.slnx")))
                return dir.FullName;
        }

        return Environment.CurrentDirectory;
    }

    private static string DefaultFuse(string fuseRoot)
    {
        var exe = OperatingSystem.IsWindows() ? "fuse.exe" : "fuse";
        var release = Path.Combine(fuseRoot, "src", "Fuse", "bin", "Release", "net10.0", exe);
        return File.Exists(release) ? release : Path.Combine(fuseRoot, "src", "Fuse", "bin", "Debug", "net10.0", exe);
    }

    /// <summary>The solution to build: a known one for the pinned eval repositories, else the only solution at the root.</summary>
    private static string DefaultSolution(string repo)
    {
        foreach (var known in new[] { "src/NodaTime.slnx", "eShopOnWeb.sln", "Fixture.sln" })
        {
            if (File.Exists(Path.Combine(repo, known)))
                return known;
        }

        var candidates = Directory.GetFiles(repo, "*.sln*").Select(Path.GetFileName).OfType<string>().ToList();
        return candidates.Count == 1 ? candidates[0] : throw new ArgumentException($"pass --solution; found {string.Join(", ", candidates)}");
    }
}
