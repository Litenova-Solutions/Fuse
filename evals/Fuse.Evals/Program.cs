using System.Text.Json;
using Fuse.Dotnet;

namespace Fuse.Evals;

/// <summary>
///     The eval suites. Every measurement of Fuse goes through the fuse executable; the truth side is a real dotnet build
///     or dotnet test. The chart command renders site/benefits.svg from the recorded results.
/// </summary>
internal static class Program
{
    private const string Usage = """
        usage: Fuse.Evals <suite> <repo> [--mutations N] [--seed S] [--solution path] [--fuse path]
               Fuse.Evals chart
               Fuse.Evals clone <repo>
               Fuse.Evals clean
          suite: correctness | selection | latency | all
          repo:  fixture (generated under evals/.work/fixture), a pinned repository name, or a path to a git repository
          clone: NodaTime | Jellyfin | CommunityToolkit, checked out at the pinned commit under
                 %LOCALAPPDATA%/fuse/evals/repos (outside this repository, so its build settings do not leak in)
          clean: deletes %LOCALAPPDATA%/fuse/evals, which holds every checkout; the generated fixture in
                 evals/.work/fixture and the result files in evals/results stay
        """;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<int> Main(string[] args)
    {
        if (args is ["chart"])
        {
            var root = FindFuseRoot();
            var target = Path.Combine(root, "site", "benefits.svg");
            await File.WriteAllTextAsync(target, ChartRenderer.Render(root));
            Console.WriteLine($"wrote {Path.GetRelativePath(root, target)}");
            return 0;
        }

        if (args is ["clone", var wanted])
            return await CloneAsync(wanted);

        if (args is ["clean"])
            return CleanAsync();

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

        var repoPath = await ResolveAsync(args[1], fuseRoot);
        var solutionPath = options.GetValueOrDefault("solution") ?? DefaultSolution(repoPath);
        var repo = new EvalRepo(repoPath, fuse, solutionPath);
        var solution = SolutionInfo.Load(repoPath, solutionPath);
        Console.WriteLine($"repo {repoPath}, solution {solutionPath}: {solution.CodeProjects.Count} code project(s), {solution.TestProjects.Count} test project(s); fuse {fuse}");

        if (!await RestoreAsync(repoPath, solutionPath, solution))
        {
            Console.Error.WriteLine("restore failed");
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

    /// <summary>
    ///     Restores the solution, then every project on disk that the solution leaves out. Fuse reads every project it
    ///     finds in the working tree, not only the solution's, and refuses to answer while one of them is unrestored, so a
    ///     restore that stops at the solution leaves the repository in a state no user would be in.
    /// </summary>
    private static async Task<bool> RestoreAsync(string repoPath, string solutionPath, SolutionInfo solution)
    {
        var restore = await ProcessRunner.RunAsync("dotnet", ["restore", solutionPath, "-nologo", "-v:q"], repoPath, CancellationToken.None);
        if (restore.ExitCode != 0)
        {
            Console.Error.WriteLine(restore.Output);
            return false;
        }

        var outside = ProjectsOutside(repoPath, solution);
        foreach (var project in outside)
        {
            var result = await ProcessRunner.RunAsync("dotnet", ["restore", project, "-nologo", "-v:q"], repoPath, CancellationToken.None);
            if (result.ExitCode != 0)
            {
                Console.Error.WriteLine($"restore failed for {project}, which the solution leaves out:\n{result.Output}");
                return false;
            }
        }

        if (outside.Count > 0)
            Console.WriteLine($"restored {outside.Count} project(s) outside {solutionPath}: {string.Join(", ", outside.Select(Path.GetFileName))}");
        return true;
    }

    /// <summary>
    ///     Deletes the evals' state directory, which holds the cloned checkouts. The generated fixture lives in the Fuse
    ///     checkout and stays, and so do the result files, since they are what the documentation quotes.
    /// </summary>
    private static int CleanAsync()
    {
        if (!Directory.Exists(PinnedRepo.StateDirectory))
        {
            Console.WriteLine($"nothing to clean at {PinnedRepo.StateDirectory}");
            return 0;
        }

        PinnedRepo.Clean();
        Console.WriteLine($"removed {PinnedRepo.StateDirectory}");
        return 0;
    }

    /// <summary>Project files under the repository that the solution does not list, in a stable order.</summary>
    private static List<string> ProjectsOutside(string repoPath, SolutionInfo solution)
    {
        var listed = solution.Projects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(repoPath, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
            .Where(f => !listed.Contains(Path.GetFullPath(f)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The repository to measure: the generated fixture, a pinned repository already cloned, or any path.</summary>
    private static async Task<string> ResolveAsync(string nameOrPath, string fuseRoot)
    {
        if (nameOrPath == "fixture")
            return await FixtureGenerator.CreateAsync(Path.Combine(fuseRoot, "evals", ".work", "fixture"));
        if (PinnedRepo.Find(nameOrPath) is { } pinned && Directory.Exists(pinned.WorkPath()))
            return pinned.WorkPath();
        return Path.GetFullPath(nameOrPath);
    }

    /// <summary>
    ///     Checks a pinned repository out under the evals' own state directory at its commit, from its remote or from a
    ///     local clone it cannot be cloned from, and copies the files the build needs that git does not carry. A checkout
    ///     already at that commit is left alone, so re-running this costs nothing.
    /// </summary>
    private static async Task<int> CloneAsync(string name)
    {
        if (PinnedRepo.Find(name) is not { Commit: { Length: > 0 } commit } pinned)
        {
            Console.Error.WriteLine($"no pinned repository called {name}; clone one of {string.Join(", ", PinnedRepo.All.Select(r => r.Name))}");
            return 2;
        }

        var path = pinned.WorkPath();
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        var head = Directory.Exists(Path.Combine(path, ".git")) ? await GitAsync(path, "rev-parse", "HEAD") : null;
        if (head is not { ExitCode: 0 } || head.Output.Trim() != commit)
        {
            if (head is null)
                await GitOrFail(parent, "clone", "--quiet", pinned.Url ?? throw new ArgumentException($"{pinned.Name} has no source"), path);
            else
                await GitOrFail(path, "fetch", "--quiet", "origin");
            await GitOrFail(path, "checkout", "--quiet", "--detach", commit);
        }

        var now = await GitAsync(path, "rev-parse", "HEAD");
        Console.WriteLine($"{pinned.Name} at {now.Output.Trim()} in {path}");
        return now.Output.Trim() == commit ? 0 : 1;
    }

    private static async Task<ProcessResult> GitAsync(string workingDirectory, params string[] args) =>
        await ProcessRunner.RunAsync("git", args, workingDirectory, CancellationToken.None);

    private static async Task GitOrFail(string workingDirectory, params string[] args)
    {
        var result = await GitAsync(workingDirectory, args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed:\n{result.Output}");
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

    /// <summary>The solution to build: the pinned one for a known repository, else the only solution at the root.</summary>
    private static string DefaultSolution(string repo)
    {
        if (PinnedRepo.Find(Path.GetFileName(repo)) is { } pinned && File.Exists(Path.Combine(repo, pinned.Solution)))
            return pinned.Solution;

        var candidates = Directory.GetFiles(repo, "*.sln*").Select(Path.GetFileName).OfType<string>().ToList();
        return candidates.Count == 1 ? candidates[0] : throw new ArgumentException($"pass --solution; found {string.Join(", ", candidates)}");
    }
}
