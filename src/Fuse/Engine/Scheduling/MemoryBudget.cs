using System.Globalization;
using System.Runtime.InteropServices;
using Fuse.Paths;
namespace Fuse.Engine.Scheduling;

/// <summary>
///     The memory one engine may hold. At each request boundary the engine reads its working set and the OS's
///     memory load and sheds in a fixed order, and past the ceiling it answers
///     <see cref="Fuse.Protocol.Analysis.EngineResponse.Busy"/> instead of growing: an explicit refusal, never a narrowed
///     check. Engines on one machine record their working set in their state directory, and the engine that has
///     been idle the longest among them exits first when the machine runs short, whether from Fuse engines or
///     other processes on the machine.
/// </summary>
internal sealed class MemoryBudget
{
    /// <summary>The ceiling one engine holds to, in bytes. It is decided here, not a setting.</summary>
    public const long DefaultCeilingBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Minimum total memory (64 MB) considered a real machine rather than a synthetic unit-test fixture.</summary>
    private const long RealMachineFloorBytes = 64L * 1024 * 1024;

    private readonly Func<long> _workingSet;
    private readonly Func<long> _memoryLoad;
    private readonly Func<long> _totalAvailable;

    /// <summary>True when the load and the total come from the OS/GC together, so one snapshot reads both.</summary>
    private readonly bool _sharedMemoryInfo;

    public MemoryBudget(long ceilingBytes = DefaultCeilingBytes, Func<long>? workingSet = null, Func<long>? memoryLoad = null, Func<long>? totalAvailable = null)
    {
        CeilingBytes = Math.Max(1, ceilingBytes);
        _workingSet = workingSet ?? (() => Environment.WorkingSet);
        _memoryLoad = memoryLoad ?? (() => ReadOsMemory().MemoryLoadBytes);
        _totalAvailable = totalAvailable ?? (() => ReadOsMemory().TotalAvailableBytes);
        _sharedMemoryInfo = memoryLoad is null && totalAvailable is null;
    }

    /// <summary>The ceiling in bytes, past which a request is answered Busy.</summary>
    public long CeilingBytes { get; }

    /// <summary>What the engine holds now, against the ceiling and the machine.</summary>
    public Assessment Assess()
    {
        var workingSet = _workingSet();
        long load;
        long total;
        if (_sharedMemoryInfo)
        {
            (load, total) = ReadOsMemory();
        }
        else
        {
            load = _memoryLoad();
            total = _totalAvailable();
        }

        var byCeiling = CeilingBytes == 0 ? 0 : (double)workingSet / CeilingBytes;
        var byMachine = total > 0 ? (double)load / total : 0;
        // High machine-wide memory load (including non-Fuse processes) triggers progressive shedding of caches,
        // views and idle cone projects, while IsPastCeiling remains gated on this engine's own ceiling so other
        // programs never force one engine to refuse its callers with Busy.
        var shed = new List<ShedStep>();
        if (byCeiling >= 0.70 || byMachine >= 0.90)
            shed.Add(ShedStep.Background);
        if (byCeiling >= 0.80 || byMachine >= 0.90)
            shed.Add(ShedStep.KeptAnswersAndCaches);
        if (byCeiling >= 0.85 || byMachine >= 0.90)
            shed.Add(ShedStep.SessionViews);
        if (byCeiling >= 0.90 || byMachine >= 0.92)
            shed.Add(ShedStep.HeadCompilations);
        if (byCeiling >= 0.95 || byMachine >= 0.95)
            shed.Add(ShedStep.IdleConeProjects);
        return new Assessment(workingSet, CeilingBytes, load, total, byCeiling >= 1.0, shed);
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
                Path.Combine(stateDirectory, LocalState.EngineMemoryFile),
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
            var file = Path.Combine(directory, LocalState.EngineMemoryFile);
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
                if (pid <= 0 || workingSet < 0 || idleMs < 0)
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
    ///     True when the machine holds more than 90 percent of available memory (from Fuse engines or OS memory load)
    ///     and this engine has been idle at least as long as every other, so it exits first and a busier engine stays.
    /// </summary>
    public static bool ShouldExitIdlest(IReadOnlyList<EngineRecord> engines, int pid, long totalAvailable)
    {
        if (totalAvailable <= 0 || engines.Count == 0)
            return false;
        if (!IsMachineShort(engines, totalAvailable))
            return false;
        // The idlest engine yields; a busier engine stays.
        return IsIdlest(engines, pid);
    }

    /// <summary>
    ///     True when the machine holds more than 90 percent of <paramref name="totalAvailable"/> (comparing both Fuse
    ///     engines and <paramref name="memoryLoadBytes"/>) and this engine has been idle at least as long as every other.
    /// </summary>
    public static bool ShouldExitIdlest(IReadOnlyList<EngineRecord> engines, int pid, long totalAvailable, long memoryLoadBytes)
    {
        if (totalAvailable <= 0 || engines.Count == 0)
            return false;
        if (!IsMachineShort(engines, totalAvailable, memoryLoadBytes))
            return false;
        return IsIdlest(engines, pid);
    }

    /// <summary>
    ///     True when the machine holds more than 90 percent of available memory, incorporating actual OS memory load
    ///     when <paramref name="totalAvailable"/> reflects real machine memory.
    /// </summary>
    public static bool IsMachineShort(IReadOnlyList<EngineRecord> engines, long totalAvailable)
    {
        if (totalAvailable <= 0)
            return false;
        if (totalAvailable >= RealMachineFloorBytes)
        {
            var (osLoad, osTotal) = ReadOsMemory();
            var effectiveTotal = osTotal > 0 ? Math.Max(totalAvailable, osTotal) : totalAvailable;
            return IsMachineShort(engines, effectiveTotal, osLoad);
        }

        return IsMachineShort(engines, totalAvailable, memoryLoadBytes: 0);
    }

    /// <summary>True when either the Fuse engines or the OS <paramref name="memoryLoadBytes"/> exceeds 90 percent of <paramref name="totalAvailable"/>.</summary>
    public static bool IsMachineShort(IReadOnlyList<EngineRecord> engines, long totalAvailable, long memoryLoadBytes) =>
        totalAvailable > 0 && EffectiveMachineLoad(engines, memoryLoadBytes) > totalAvailable * 0.9;

    /// <summary>
    ///     True when the machine holds more than 70 percent of available memory, incorporating actual OS memory load
    ///     when <paramref name="totalAvailable"/> reflects real machine memory.
    /// </summary>
    public static bool MachineNeedsMemory(IReadOnlyList<EngineRecord> engines, long totalAvailable)
    {
        if (totalAvailable <= 0)
            return false;
        if (totalAvailable >= RealMachineFloorBytes)
        {
            var (osLoad, osTotal) = ReadOsMemory();
            var effectiveTotal = osTotal > 0 ? Math.Max(totalAvailable, osTotal) : totalAvailable;
            return MachineNeedsMemory(engines, effectiveTotal, osLoad);
        }

        return MachineNeedsMemory(engines, totalAvailable, memoryLoadBytes: 0);
    }

    /// <summary>True when either the Fuse engines or the OS <paramref name="memoryLoadBytes"/> exceeds 70 percent of <paramref name="totalAvailable"/>.</summary>
    public static bool MachineNeedsMemory(IReadOnlyList<EngineRecord> engines, long totalAvailable, long memoryLoadBytes) =>
        totalAvailable > 0 && EffectiveMachineLoad(engines, memoryLoadBytes) > totalAvailable * 0.7;

    private static long EffectiveMachineLoad(IReadOnlyList<EngineRecord> engines, long memoryLoadBytes) =>
        Math.Max(engines.Sum(e => e.WorkingSetBytes), Math.Max(0, memoryLoadBytes));

    /// <summary>
    ///     Reads the current OS memory load and total physical/available memory in bytes, using
    ///     <c>GlobalMemoryStatusEx</c> on Windows, <c>/proc/meminfo</c> on Linux, and <see cref="GC.GetGCMemoryInfo()"/>
    ///     as fallback so <c>DOTNET_GCHeapHardLimit</c> never shrinks the denominator or leaves load at zero.
    /// </summary>
    internal static (long MemoryLoadBytes, long TotalAvailableBytes) ReadOsMemory()
    {
        if (OperatingSystem.IsWindows() && TryReadWindowsMemory(out var winLoad, out var winTotal))
            return (winLoad, winTotal);
        if (OperatingSystem.IsLinux() && TryReadProcMemInfo(out var linuxLoad, out var linuxTotal))
            return (linuxLoad, linuxTotal);

        var info = GC.GetGCMemoryInfo();
        var total = info.TotalAvailableMemoryBytes;
        // When DOTNET_GCHeapHardLimit is configured, TotalAvailableMemoryBytes is clamped to the heap cap while
        // HighMemoryLoadThresholdBytes still reflects 90% of machine memory.
        if (info.HighMemoryLoadThresholdBytes > total)
            total = (long)(info.HighMemoryLoadThresholdBytes / 0.9);
        return (Math.Max(0, info.MemoryLoadBytes), Math.Max(0, total));
    }

    private static bool TryReadWindowsMemory(out long loadBytes, out long totalBytes)
    {
        loadBytes = 0;
        totalBytes = 0;
        try
        {
            var status = new Native.MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<Native.MemoryStatusEx>() };
            if (!Native.GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
                return false;
            totalBytes = (long)Math.Min(status.ullTotalPhys, (ulong)long.MaxValue);
            var avail = (long)Math.Min(status.ullAvailPhys, (ulong)totalBytes);
            loadBytes = Math.Max(0, totalBytes - avail);
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryReadProcMemInfo(out long loadBytes, out long totalBytes)
    {
        loadBytes = 0;
        totalBytes = 0;
        try
        {
            if (!File.Exists("/proc/meminfo"))
                return false;
            long totalKb = -1;
            long availableKb = -1;
            long freeKb = -1;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    totalKb = ParseMemInfoKb(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    availableKb = ParseMemInfoKb(line);
                else if (line.StartsWith("MemFree:", StringComparison.Ordinal))
                    freeKb = ParseMemInfoKb(line);
                if (totalKb >= 0 && availableKb >= 0)
                    break;
            }

            if (totalKb <= 0)
                return false;
            var availKb = availableKb >= 0 ? availableKb : Math.Max(0, freeKb);
            totalBytes = totalKb * 1024;
            var info = GC.GetGCMemoryInfo();
            // Honor smaller container cgroup limits when no GCHeapHardLimit clamps TotalAvailableMemoryBytes.
            if (Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit") is null
                && info.TotalAvailableMemoryBytes > 0
                && info.TotalAvailableMemoryBytes < totalBytes)
            {
                totalBytes = info.TotalAvailableMemoryBytes;
                if (info.MemoryLoadBytes > 0)
                {
                    loadBytes = info.MemoryLoadBytes;
                    return true;
                }
            }

            loadBytes = Math.Max(0, (totalKb - Math.Min(totalKb, availKb)) * 1024);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static long ParseMemInfoKb(string line)
    {
        var colon = line.IndexOf(':');
        if (colon < 0)
            return -1;
        var rest = line.AsSpan(colon + 1).Trim();
        var space = rest.IndexOf(' ');
        var number = space >= 0 ? rest[..space] : rest;
        return long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
    }

    /// <summary>
    ///     True when <paramref name="pid"/> yields first: it has been idle at least as long as every other engine,
    ///     and among engines idle as long it holds the most, with the highest process id last, so only one engine
    ///     yields at a time and a busier engine stays.
    /// </summary>
    public static bool IsIdlest(IReadOnlyList<EngineRecord> engines, int pid)
    {
        var self = engines.FirstOrDefault(e => e.Pid == pid);
        if (self is null)
            return false;
        foreach (var other in engines)
        {
            if (other.Pid == pid)
                continue;
            if (other.Idle > self.Idle)
                return false;
            if (other.Idle == self.Idle && (other.WorkingSetBytes > self.WorkingSetBytes || (other.WorkingSetBytes == self.WorkingSetBytes && other.Pid > pid)))
                return false;
        }

        return true;
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
    internal sealed record Assessment(long WorkingSetBytes, long CeilingBytes, long MemoryLoadBytes, long TotalAvailableBytes, bool IsPastCeiling, IReadOnlyList<ShedStep> Shed);

    /// <summary>One engine's record on the machine: its process, its working set and how long it has been idle.</summary>
    /// <param name="Pid">The engine process's id.</param>
    /// <param name="WorkingSetBytes">Its working set in bytes.</param>
    /// <param name="Idle">How long it has served no request.</param>
    internal sealed record EngineRecord(int Pid, long WorkingSetBytes, TimeSpan Idle);
}
