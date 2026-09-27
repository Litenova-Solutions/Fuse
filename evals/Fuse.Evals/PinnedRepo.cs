namespace Fuse.Evals;

/// <summary>
///     A repository the evals measure: where to get it, the commit to measure, the solution to build and the label the
///     chart draws for it. A result file names the repository, and the commit is what makes its numbers reproducible later.
/// </summary>
internal sealed record PinnedRepo(string Name, string? Url, string? Commit, string Solution, string Label)
{
    /// <summary>The generated fixture: built on the spot, so it has no URL and no commit.</summary>
    public static PinnedRepo Fixture { get; } = new("fixture", null, null, "Fixture.sln", "Small solution, 5 projects");

    /// <summary>The cloned repositories, in the order the chart draws them.</summary>
    public static IReadOnlyList<PinnedRepo> All { get; } =
    [
        new("NodaTime", "https://github.com/nodatime/NodaTime", "fcd80e11216ba403ccce0abbcedc41ba37bb352e", "src/NodaTime.slnx", "NodaTime, 17 projects"),
        new("Jellyfin", "https://github.com/jellyfin/Jellyfin", "ee91c75e777da41a9c4f4855e70adc604fbf2ef8", "Jellyfin.sln", "Jellyfin, 41 projects"),
        new("LiteBus", "https://github.com/litenova/LiteBus", "a1a6bec0bee5e096e33d53747a4dc82f7e609e73", "LiteBus.slnx", "LiteBus, 101 projects"),
    ];

    /// <summary>Every repository the chart draws: the fixture first, then the cloned ones.</summary>
    public static IReadOnlyList<PinnedRepo> Charted { get; } = [Fixture, .. All];

    public static PinnedRepo? Find(string name) => Charted.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where a clone of this repository lives (the four eval repositories sit side by side under evals/.work/repos).</summary>
    public string WorkPath(string fuseRoot) => Path.Combine(fuseRoot, "evals", ".work", "repos", Name);
}
