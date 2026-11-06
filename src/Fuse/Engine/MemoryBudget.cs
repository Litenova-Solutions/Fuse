namespace Fuse.Engine;

/// <summary>
///     The memory one engine may hold. At each request boundary the engine reads its working set and the GC's
///     memory load and sheds in a fixed order, and past the ceiling it answers
///     <see cref="Fuse.Protocol.EngineResponse.Busy"/> instead of growing: an explicit refusal, never a narrowed
///     check. Engines on one machine record their working set in their state directory, and the engine that has
///     been idle the longest among them exits first when the machine runs short.
/// </summary>
internal sealed class MemoryBudget
{
    /// <summary>The ceiling one engine holds to, in bytes. It is decided here, not a setting.</summary>
    public const long DefaultCeilingBytes = 2L * 1024 * 1024 * 1024;

    private readonly Func<long> _workingSet;
    private readonly Func<long> _memoryLoad;
    private readonly Func<long> _totalAvailable;

    public MemoryBudget(long ceilingBytes = DefaultCeilingBytes, Func<long>? workingSet = null, Func<long>? memoryLoad = null, Func<long>? totalAvailable = null)
    {
        CeilingBytes = Math.Max(1, ceilingBytes);
        _workingSet = workingSet ?? (() => Environment.WorkingSet);
        _memoryLoad = memoryLoad ?? (() => GC.GetGCMemoryInfo().MemoryLoadBytes);
        _totalAvailable = totalAvailable ?? (() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    }

    /// <summary>The ceiling in bytes, past which a request is answered Busy.</summary>
    public long CeilingBytes { get; }

    /// <summary>What the engine holds now, against the ceiling and the machine.</summary>
    public Snapshot Assess()
    {
        var workingSet = _workingSet();
        var load = _memoryLoad();
        var total = _totalAvailable();
        var byCeiling = CeilingBytes == 0 ? 0 : (double)workingSet / CeilingBytes;
        var byMachine = total > 0 ? (double)load / total : 0;
        var pressure = Math.Max(byCeiling, byMachine);
        var shed = new List<ShedStep>();
        if (pressure >= 0.70)
            shed.Add(ShedStep.Background);
        if (pressure >= 0.80)
            shed.Add(ShedStep.KeptAnswersAndCaches);
        if (pressure >= 0.85)
            shed.Add(ShedStep.SessionViews);
        if (pressure >= 0.90)
            shed.Add(ShedStep.HeadCompilations);
        if (pressure >= 0.95)
            shed.Add(ShedStep.IdleConeProjects);
        return new Snapshot(workingSet, CeilingBytes, load, total, pressure >= 1.0, shed);
    }

    /// <summary>
    ///     Records this engine's working set and idle time in <paramref name="stateDirectory"/>, as
    ///     <c>engine-memory.txt</c>, for the machine budget. A record that cannot be written is skipped.
    /// </summary>
    public void Record(string stateDirectory, TimeSpan idle)
    {
        try
        {
            Directory.CreateDirectory(stateDirectory);
            var snapshot = Assess();
            File.WriteAllText(
                Path.Combine(stateDirectory, "engine-memory.txt"),
                $"{Environment.ProcessId}\t{snapshot.WorkingSetBytes}\t{(long)idle.TotalMilliseconds}\t{DateTime.UtcNow.Ticks}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The record is for other engines; one that cannot write it still answers its own requests.
        }
    }

    /// <summary>
    ///     The records of every engine under <paramref name="repositories"/>, read within the last minute. A record
    ///     that does not read is left out.
    /// </summary>
    public static IReadOnlyList<EngineRecord> ReadMachine(string repositories)
    {
        var records = new List<EngineRecord>();
        if (!Directory.Exists(repositories))
            return records;
        foreach (var directory in Directory.EnumerateDirectories(repositories))
        {
            var file = Path.Combine(directory, "engine-memory.txt");
            try
            {
                if (!File.Exists(file))
                    continue;
                var parts = File.ReadAllText(file).Split('\t');
                if (parts.Length != 4
                    || !int.TryParse(parts[0], out var pid)
                    || !long.TryParse(parts[1], out var workingSet)
                    || !long.TryParse(parts[2], out var idleMs)
                    || !long.TryParse(parts[3], out var ticks))
                    continue;
                if (DateTime.UtcNow.Ticks - ticks > TimeSpan.FromMinutes(1).Ticks)
                    continue;
                records.Add(new EngineRecord(pid, workingSet, TimeSpan.FromMilliseconds(idleMs)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // An engine writing its record holds it only briefly; the next read sees it.
            }
        }

        return records;
    }

    /// <summary>
    ///     True when the engines on the machine hold more than 90 percent of the memory the GC reports and this
    ///     engine has been idle at least as long as every other, so it exits first and a busier engine stays.
    /// </summary>
    public static bool ShouldExitIdlest(IReadOnlyList<EngineRecord> engines, int pid, long totalAvailable)
    {
        if (totalAvailable <= 0 || engines.Count == 0)
            return false;
        if (engines.Sum(e => e.WorkingSetBytes) <= totalAvailable * 0.9)
            return false;
        var self = engines.FirstOrDefault(e => e.Pid == pid);
        return self is not null && engines.All(e => self.Idle >= e.Idle);
    }

    /// <summary>One step the engine sheds, in the order it sheds them.</summary>
    internal enum ShedStep
    {
        /// <summary>Background checking and warming stop first.</summary>
        Background,

        /// <summary>Kept answers and caches go next.</summary>
        KeptAnswersAndCaches,

        /// <summary>Session views go after them.</summary>
        SessionViews,

        /// <summary>HEAD compilations go after session views.</summary>
        HeadCompilations,

        /// <summary>Idle cone projects go last, before the ceiling refuses a request.</summary>
        IdleConeProjects,
    }

    /// <summary>What the engine holds at one request boundary, and what it sheds at that pressure.</summary>
    /// <param name="WorkingSetBytes">The process working set in bytes.</param>
    /// <param name="CeilingBytes">The per-engine ceiling in bytes.</param>
    /// <param name="MemoryLoadBytes">The GC memory load in bytes.</param>
    /// <param name="TotalAvailableBytes">The memory available to the GC in bytes, or 0 when it reports none.</param>
    /// <param name="IsPastCeiling">True when the engine is at or past its ceiling and answers Busy.</param>
    /// <param name="Shed">The shed steps for this pressure, in the order they are taken.</param>
    internal sealed record Snapshot(long WorkingSetBytes, long CeilingBytes, long MemoryLoadBytes, long TotalAvailableBytes, bool IsPastCeiling, IReadOnlyList<ShedStep> Shed);

    /// <summary>One engine's record on the machine: its process, its working set and how long it has been idle.</summary>
    /// <param name="Pid">The engine process's id.</param>
    /// <param name="WorkingSetBytes">Its working set in bytes.</param>
    /// <param name="Idle">How long it has served no request.</param>
    internal sealed record EngineRecord(int Pid, long WorkingSetBytes, TimeSpan Idle);
}
