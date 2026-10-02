using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fuse.Evals;

/// <summary>
///     The truth side of one suite on one repository: the plan of cases, and the real <c>dotnet build</c> or
///     <c>dotnet test</c> answer for HEAD and for each case. The truth does not change between runs while its
///     <see cref="Key"/> stays the same, so a run reads it from a file and measures only Fuse, and the jobs of one run can
///     each fill a share of it and merge their files.
/// </summary>
/// <remarks>
///     The key is the suite, the repository's name and git tree, the seed, the requested count, the SDK version, and a hash
///     of the code that draws the cases and runs the truth side (<see cref="GeneratorFiles"/>). Any change to one of them
///     starts a new file.
/// </remarks>
internal sealed class TruthFile
{
    /// <summary>Changed by hand when the file's meaning changes in a way the generator hash does not see.</summary>
    public const int FormatVersion = 1;

    /// <summary>The evals source files whose content decides the cases and the truth answers.</summary>
    public static readonly string[] GeneratorFiles = ["BehaviorMutator.cs", "CompileMutator.cs", "CasePlanner.cs", "TruthRunner.cs", "SolutionInfo.cs", "TruthFile.cs"];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public required string Key { get; init; }

    public required string Suite { get; init; }

    public required string Repo { get; init; }

    public required int Seed { get; init; }

    public required int Count { get; init; }

    /// <summary>The cases, once a job has drawn them; null until then.</summary>
    public List<PlannedCase>? Plan { get; set; }

    /// <summary>The edits the selection plan drew and left out because the solution did not build with them.</summary>
    public List<string> Skipped { get; set; } = [];

    public TruthItem? Head { get; set; }

    public Dictionary<int, TruthItem> Cases { get; set; } = [];

    /// <summary>True when the plan is drawn and HEAD and every case have their answer.</summary>
    public bool IsComplete => Plan is not null && Head is not null && Plan.All(c => Cases.ContainsKey(c.Index));

    /// <summary>The key of the truth for <paramref name="suite"/> on the repository at <paramref name="tree"/>.</summary>
    public static string KeyOf(string fuseRoot, string suite, string repo, string tree, int seed, int count, string sdk)
    {
        var source = new StringBuilder();
        foreach (var name in GeneratorFiles)
        {
            // Line endings depend on the checkout, not on the code, so they are left out of the hash.
            source.Append(name).Append('\n').Append(File.ReadAllText(Path.Combine(fuseRoot, "evals", "Fuse.Evals", name)).Replace("\r", "", StringComparison.Ordinal));
        }

        var generator = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToString())))[..12];
        return $"truth-v{FormatVersion}-{suite}-{repo}-{tree[..Math.Min(12, tree.Length)]}-s{seed}-n{count}-sdk{sdk}-g{generator}";
    }

    /// <summary>The file at <paramref name="path"/> when it exists and carries <paramref name="key"/>, else null.</summary>
    public static TruthFile? Load(string path, string key)
    {
        if (!File.Exists(path))
            return null;
        var file = JsonSerializer.Deserialize<TruthFile>(File.ReadAllText(path), Json);
        return file?.Key == key ? file : null;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>
    ///     Takes the plan and the answers <paramref name="other"/> has and this file lacks. Two files with one key hold the
    ///     same plan, so the first plan seen is kept.
    /// </summary>
    public void Merge(TruthFile other)
    {
        if (other.Key != Key)
            throw new InvalidOperationException($"cannot merge truth {other.Key} into {Key}");
        if (Plan is null && other.Plan is not null)
        {
            Plan = other.Plan;
            Skipped = other.Skipped;
        }

        Head ??= other.Head;
        foreach (var (index, item) in other.Cases)
            Cases.TryAdd(index, item);
    }

    /// <summary>Where the answers were measured, each place once, for the result file.</summary>
    public List<string> Origins() =>
        [.. new[] { Head }.Concat(Cases.Values).OfType<TruthItem>().Select(i => i.Origin).Distinct().Order(StringComparer.Ordinal)];
}
