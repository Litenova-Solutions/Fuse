using Fuse.Paths;
using Fuse.Protocol;

namespace Fuse.Engine;

/// <summary>
///     The frozen declaration patterns a session guard registered (<c>fuse session guard &lt;name&gt; --freeze</c>),
///     kept in <c>guards.tsv</c> in the state directory so they survive an engine restart. A changed declaration
///     whose file falls under a frozen pattern is a violation the engine publishes to its watchers.
/// </summary>
internal sealed class SurfaceGuardStore
{
    private readonly RepoRoot _root;
    private readonly string _file;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<string>> _guards = [];

    /// <param name="root">The repository whose state directory holds the guards.</param>
    public SurfaceGuardStore(RepoRoot root)
    {
        _root = root;
        _file = Path.Combine(root.StateDirectory, "guards.tsv");
        Read();
    }

    /// <summary>
    ///     Replaces the frozen patterns of <paramref name="guard"/>'s session, or removes the session when the
    ///     request unfreezes it, then writes the record to disk.
    /// </summary>
    /// <param name="guard">The freeze or unfreeze request.</param>
    public void Apply(FreezeGuardRequest guard)
    {
        lock (_lock)
        {
            var name = guard.Name;
            if (guard.Unfreeze)
            {
                _guards.Remove(name);
            }
            else
            {
                var patterns = new List<string>();
                foreach (var pattern in guard.Patterns)
                {
                    if (PathPattern.Normalize(pattern, out var clean) && !patterns.Contains(clean, StringComparer.Ordinal))
                        patterns.Add(clean);
                }

                if (IsValidName(name) && patterns.Count > 0)
                    _guards[name] = patterns;
                else
                    _guards.Remove(name);
            }

            Write();
        }
    }

    /// <summary>
    ///     One <c>guard_violation</c> event per guard and changed declaration whose file matches one of that guard's
    ///     frozen patterns. Files not in <paramref name="changedFiles"/>, and files with no changed declarations in
    ///     <paramref name="changedDeclarations"/>, report nothing.
    /// </summary>
    /// <param name="changedFiles">Every file that differs from HEAD now.</param>
    /// <param name="changedDeclarations">The changed declaration headers of each file.</param>
    /// <returns>The violations, ordered by changed file, then guard name, then declaration.</returns>
    public IReadOnlyList<WatchEvent> Violations(IReadOnlyList<RepoPath> changedFiles, IReadOnlyDictionary<RepoPath, IReadOnlyList<string>> changedDeclarations)
    {
        lock (_lock)
        {
            var events = new List<WatchEvent>();
            foreach (var file in changedFiles)
            {
                if (!changedDeclarations.TryGetValue(file, out var declarations))
                    continue;
                var relative = file.Relative;
                foreach (var guard in _guards.OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    var matched = guard.Value.FirstOrDefault(pattern => PathPattern.Matches(pattern, relative));
                    if (matched is null)
                        continue;
                    foreach (var declaration in declarations)
                    {
                        events.Add(new WatchEvent(
                            "guard_violation",
                            GuardName: guard.Key,
                            ViolatedPattern: matched,
                            Declaration: declaration,
                            Message: $"{declaration} in {relative} changed under frozen pattern {matched} of {guard.Key}"));
                    }
                }
            }

            return events;
        }
    }

    /// <summary>Loads the guards from <c>guards.tsv</c>, skipping a line that does not read back as a name and a pattern.</summary>
    private void Read()
    {
        if (!File.Exists(_file))
            return;
        try
        {
            foreach (var line in File.ReadAllLines(_file))
            {
                var parts = line.Split('\t');
                if (parts.Length != 2 || !IsValidName(parts[0]) || !PathPattern.Normalize(parts[1], out var clean))
                    continue;
                if (!_guards.TryGetValue(parts[0], out var patterns))
                    _guards[parts[0]] = patterns = [];
                if (!patterns.Contains(clean, StringComparer.Ordinal))
                    patterns.Add(clean);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A record that cannot be read freezes nothing, which is what a check reported before guards were kept.
            _guards.Clear();
        }
    }

    /// <summary>Replaces <c>guards.tsv</c> with the guards as they are now, through a temporary file so a reader never sees half of it.</summary>
    private void Write()
    {
        try
        {
            Directory.CreateDirectory(_root.StateDirectory);
            var lines = _guards.OrderBy(g => g.Key, StringComparer.Ordinal)
                .SelectMany(g => g.Value.Order(StringComparer.Ordinal).Select(pattern => $"{g.Key}\t{pattern}"));
            var temporary = _file + ".tmp";
            File.WriteAllLines(temporary, lines);
            File.Move(temporary, _file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The guards in memory still hold; only an engine that restarts before the next write loses them.
        }
    }

    /// <summary>True when <paramref name="name"/> can be kept: 1 to 64 letters, digits, <c>-</c>, <c>_</c> or <c>.</c>.</summary>
    private static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 64 && name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
}
