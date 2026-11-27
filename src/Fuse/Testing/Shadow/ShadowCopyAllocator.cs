using System.Globalization;
using Fuse.Paths;

namespace Fuse.Testing.Shadow;

/// <summary>
///     The copies of build output one plan prepares its shadow runs in: numbered folders (<c>g1</c>, <c>g2</c> and
///     so on) under each shadow folder. One allocator serves one plan, over the shared caches its emitter shares
///     with the planner's other plans.
/// </summary>
internal sealed class ShadowCopyAllocator
{
    private readonly ShadowEmitCaches _shared;

    /// <summary>The copies this plan holds while it prepares them, released when its preparation ends.</summary>
    private readonly List<IDisposable> _holds = [];

    /// <summary>The copy this plan claimed for each shadow folder, so every target framework of one test project prepares into the same copy.</summary>
    private readonly Dictionary<string, string> _copies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where this plan stands in the shared order of plans, taken when its allocator was made; a copy answered after it is not this plan's to use or remove.</summary>
    private readonly long _sequence;

    internal ShadowCopyAllocator(ShadowEmitCaches shared)
    {
        _shared = shared;
        _sequence = shared.NextSequence();
    }

    /// <summary>
    ///     Releases the copies this plan held while it prepared them, so a later plan may write them again. Each
    ///     copy is first recorded as one this plan answered with, under the same lock that chooses copies, so a
    ///     plan that is already under way keeps it for this plan's answer instead of using or removing it.
    /// </summary>
    internal void ReleaseCopies()
    {
        lock (_shared.Allocation)
        {
            var answered = _shared.NextSequence();
            foreach (var copy in _copies.Values)
                _shared.Answered[copy] = answered;
            foreach (var hold in _holds)
                hold.Dispose();
            _holds.Clear();
            _copies.Clear();
        }
    }

    /// <summary>
    ///     Chooses the copy of <paramref name="project"/> this plan prepares and holds it until the plan's
    ///     preparation ends, so a plan preparing at the same time chooses another copy and neither writes the
    ///     copy the other answers with. The choice itself is serialized in the shared state, briefly.
    /// </summary>
    internal string HoldCopy(string project, Action<string> log)
    {
        lock (_shared.Allocation)
        {
            if (_copies.TryGetValue(project, out var claimed))
                return claimed;
            var chosen = CopyFolder(project, log);
            Directory.CreateDirectory(chosen);
            _holds.Add(LocalState.HoldTestCopy(chosen));
            _copies[project] = chosen;
            return chosen;
        }
    }

    /// <summary>
    ///     The folder a plan prepares a test project's shadow run in, under <paramref name="project"/>: the newest copy when
    ///     no test run holds it and no plan answered with it while this plan was under way, so the files already in
    ///     place are not written again, or a new copy when a run still runs from it
    ///     (<see cref="LocalState.HoldTestCopy"/>) or a plan under way at the same time answered with it, so each
    ///     answer keeps the files it names. Every older copy is deleted once no run holds it, a run has held it
    ///     since a plan answered with it, and it is not the copy chosen here.
    /// </summary>
    /// <remarks>
    ///     Copies are numbered folders, <c>g1</c>, <c>g2</c> and so on, the newest with the highest number. Anything else in
    ///     <paramref name="project"/> is left from a version of Fuse that kept one copy per test project, and is deleted.
    ///     The client holds <c>build.lock</c> from before it asks for a plan until it holds the copies the plan names, so
    ///     no run takes a copy between this choice and the plan's answer.
    /// </remarks>
    private string CopyFolder(string project, Action<string> log)
    {
        Directory.CreateDirectory(project);
        var copies = new List<(int Number, string Path)>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(project))
        {
            var name = Path.GetFileName(entry);
            if (name.Length > 1 && name[0] == 'g' && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && Directory.Exists(entry))
                copies.Add((number, entry));
            else
                TryDelete(entry);
        }

        var newest = copies.Count == 0 ? (Number: 0, Path: null) : copies.MaxBy(c => c.Number);
        var chosen = newest.Path;
        var held = chosen is not null && LocalState.IsInUse(chosen);
        var answered = chosen is not null && _shared.Answered.TryGetValue(chosen, out var answeredAt) && answeredAt > _sequence;
        if (chosen is null || held || answered)
        {
            chosen = Path.Combine(project, $"g{newest.Number + 1}");
            Directory.CreateDirectory(chosen);
            if (held)
                log($"{Path.GetFileName(project)}: a test run still runs from {Path.GetFileName(newest.Path)}, so this plan copies into {Path.GetFileName(chosen)}");
        }

        foreach (var (_, path) in copies)
        {
            if (path == chosen)
                continue;
            if (LocalState.IsInUse(path))
            {
                // A run holding a copy a plan answered with has taken that answer, so a later choice may
                // remove the copy once nothing holds it; a plan preparing it answers with it again at the
                // end of its preparation, which records it anew.
                _shared.Answered.TryRemove(path, out _);
                continue;
            }

            if (_shared.Answered.ContainsKey(path))
                continue;
            TryDelete(path);
        }

        return chosen;
    }

    /// <summary>Deletes a file or folder left in a shadow folder; one a process still holds stays until a later plan.</summary>
    internal static void TryDelete(string entry)
    {
        try
        {
            if (Directory.Exists(entry))
                Directory.Delete(entry, recursive: true);
            else
                File.Delete(entry);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
