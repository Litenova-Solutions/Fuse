namespace Fuse.Evals;

/// <summary>
///     A repository the evals measure: where to get it, the commit to measure, the solution to build and the labels the
///     chart draws for it. A result file names the repository, and the commit is what makes its numbers reproducible later.
///     The labels describe the pinned commit, so they are true as long as the pin stands.
/// </summary>
/// <param name="Name">Folder name under evals/.work/repos, and the name every result file carries.</param>
/// <param name="Url">Where to clone from.</param>
/// <param name="LocalPath">
///     An existing local clone to take the checkout from instead of <paramref name="Url"/>, for a repository that cannot
///     build from a fresh clone. Null when the remote is enough.
/// </param>
/// <param name="LocalFiles">
///     Files the build needs that git does not carry, copied next to the checkout. Null when there are none.
/// </param>
/// <param name="Commit">The commit to check out.</param>
/// <param name="Solution">The solution the truth side builds.</param>
/// <param name="CheckLabel">Row label in the chart's check group.</param>
/// <param name="TestLabel">Row label in the chart's test group.</param>
internal sealed record PinnedRepo(
    string Name,
    string? Url,
    string? LocalPath,
    IReadOnlyList<string>? LocalFiles,
    string Commit,
    string Solution,
    string CheckLabel,
    string TestLabel)
{
    /// <summary>The generated fixture: built on the spot, so it has no source and no commit.</summary>
    public static PinnedRepo Fixture { get; } = new("fixture", null, null, null, "", "Fixture.sln", "Small solution, 5 projects", "Small solution, 5 projects");

    /// <summary>The cloned repositories, in the order the chart draws them.</summary>
    public static IReadOnlyList<PinnedRepo> All { get; } =
    [
        new("NodaTime", "https://github.com/nodatime/NodaTime", null, null, "fcd80e11216ba403ccce0abbcedc41ba37bb352e", "src/NodaTime.slnx", "NodaTime, 17 projects", "NodaTime, 42,681 tests"),
        // Pinned to the last commit before Jellyfin moved to Roslyn 5: its in-repo analyzer is built against a newer
        // compiler than any SDK that resolves on a machine whose newest SDK is 10.0.112, and Roslyn refuses that (CS9057).
        new("Jellyfin", "https://github.com/jellyfin/Jellyfin", null, null, "1d7c6af520da5c84ceac1c21a1d2da34837540ac", "Jellyfin.sln", "Jellyfin, 40 projects", "Jellyfin, 16 test projects"),
        // LiteBus signs every assembly only when LiteBus.snk sits at the repository root, and that key is not in the
        // repository, so a fresh clone cannot build (CS0281 on every cross-assembly InternalsVisibleTo).
        new("LiteBus", "https://github.com/litenova/LiteBus", @"C:\Projects\LiteBus", ["LiteBus.snk"], "a1a6bec0bee5e096e33d53747a4dc82f7e609e73", "LiteBus.slnx", "LiteBus, 101 projects", "LiteBus, 16 test projects"),
    ];

    /// <summary>Every repository the chart draws: the fixture first, then the cloned ones.</summary>
    public static IReadOnlyList<PinnedRepo> Charted { get; } = [Fixture, .. All];

    public static PinnedRepo? Find(string name) => Charted.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where a checkout of this repository lives (the eval repositories sit side by side under evals/.work/repos).</summary>
    public string WorkPath(string fuseRoot) => Path.Combine(fuseRoot, "evals", ".work", "repos", Name);
}
