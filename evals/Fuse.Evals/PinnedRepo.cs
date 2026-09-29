namespace Fuse.Evals;

/// <summary>
///     A repository the evals measure: where to get it, the commit to measure, the solution to build and the labels the
///     chart draws for it. A result file names the repository, and the commit is what makes its numbers reproducible later.
///     The labels describe the pinned commit, so they are true as long as the pin stands.
/// </summary>
/// <param name="Name">Folder name under the evals' state directory, and the name every result file carries.</param>
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
        // The counts are the .csproj entries of the pinned solution whose files exist, which is what the suites evaluate.
        new("NodaTime", "https://github.com/nodatime/NodaTime", null, null, "fcd80e11216ba403ccce0abbcedc41ba37bb352e", "src/NodaTime.slnx", "NodaTime, 15 projects", "NodaTime, 42,700 tests"),
        // Pinned to the final commit before Jellyfin moved to Roslyn 5: its in-repo analyzer is built against a newer
        // compiler than any SDK that resolves on a machine whose newest SDK is 10.0.112, and Roslyn refuses that (CS9057).
        new("Jellyfin", "https://github.com/jellyfin/Jellyfin", null, null, "1d7c6af520da5c84ceac1c21a1d2da34837540ac", "Jellyfin.sln", "Jellyfin, 40 projects", "Jellyfin, 2,535 tests"),
        // The .NET Community Toolkit: 26 projects, 13 of them test projects, layered as Common, Diagnostics,
        // HighPerformance and Mvvm, with one generator and code-fix set per supported Roslyn version. It is the fourth
        // repository because its test projects reach disjoint slices of the code, so affected-test selection can narrow.
        new("CommunityToolkit", "https://github.com/CommunityToolkit/dotnet", null, null, "b135626dd54d33b8f05f2ff31591592c004aa848", "dotnet.slnx", "Community Toolkit, 26 projects", "Community Toolkit, 12,449 tests"),
    ];

    /// <summary>Deletes <see cref="StateDirectory"/>, which holds the checkouts; the generated fixture is not in it.</summary>
    public static void Clean() => Directory.Delete(StateDirectory, recursive: true);

    /// <summary>Every repository the chart draws: the fixture first, then the cloned ones.</summary>
    public static IReadOnlyList<PinnedRepo> Charted { get; } = [Fixture, .. All];

    public static PinnedRepo? Find(string name) => Charted.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     Where a checkout of this repository lives. Outside the Fuse tree on purpose: a repository with no
    ///     <c>Directory.Packages.props</c> of its own inherits the one above it, so a checkout inside the Fuse repository
    ///     builds against Fuse's central package versions and fails to restore.
    /// </summary>
    public string WorkPath() => Path.Combine(StateDirectory, "repos", Name);

    /// <summary>Where the evals keep their own state, like the engine's.</summary>
    public static string StateDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fuse", "evals");
}
