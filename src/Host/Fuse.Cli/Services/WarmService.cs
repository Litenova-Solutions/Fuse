using System.Runtime.InteropServices;
using System.Text.Json;
using Fuse.Cli.Serialization;
using Fuse.Reduction.Caching;

namespace Fuse.Cli.Services;

/// <summary>
///     The opt-in always-on warm service (R40, Layer 3): a background OS service (Windows service, launchd agent,
///     systemd user unit) installed only by an explicit <c>fuse warm --service install</c> - never by
///     <c>fuse mcp install</c> - that keeps a store-backed daemon alive and re-warms a bounded LRU of
///     recently-used repos so they are ready before a session opens. Guardrails, all enforced here: store-backed
///     only (never a resident Roslyn compilation), a hard LRU cap, battery/load awareness (pause under battery or
///     high load), idle-evict, and a clean uninstall. It stays opt-in for the agent-first default (R38/R39 cover
///     the agent case); a promotion to opt-out requires measuring idle RSS/CPU/battery and showing they are light.
/// </summary>
public static class WarmService
{
    /// <summary>The service name registered with the OS.</summary>
    public const string ServiceName = "fuse-warm";
}

/// <summary>
///     A bounded, most-recently-used set of warm repos (R40). The service pre-warms only the last <see cref="Cap" />
///     repos opened this cycle; a repo not opened this cycle is never pre-warmed, so the warm set never grows
///     without bound.
/// </summary>
public sealed class WarmServiceLru
{
    /// <summary>The default hard cap on the number of warm repos.</summary>
    public const int DefaultCap = 5;

    private readonly LinkedList<string> _order = new(); // front = most recent.
    private readonly Dictionary<string, LinkedListNode<string>> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _cap;

    /// <summary>Initializes a new instance of the <see cref="WarmServiceLru" /> class.</summary>
    /// <param name="cap">The hard cap on warm repos (defaults to <see cref="DefaultCap" />).</param>
    public WarmServiceLru(int cap = DefaultCap) => _cap = cap > 0 ? cap : DefaultCap;

    /// <summary>The hard cap on warm repos.</summary>
    public int Cap => _cap;

    /// <summary>The warm repos, most recent first.</summary>
    public IReadOnlyList<string> Repos => _order.ToList();

    /// <summary>
    ///     Records a repo as most-recently-used, evicting the least-recently-used repos beyond the cap.
    /// </summary>
    /// <param name="root">The repo root that was opened.</param>
    /// <returns>The roots evicted by this touch (their pre-warm should stop), most recently evicted last.</returns>
    public IReadOnlyList<string> Touch(string root)
    {
        var key = System.IO.Path.GetFullPath(root);
        if (_index.TryGetValue(key, out var existing))
        {
            _order.Remove(existing);
            _order.AddFirst(existing);
        }
        else
        {
            _index[key] = _order.AddFirst(key);
        }

        var evicted = new List<string>();
        while (_order.Count > _cap)
        {
            var last = _order.Last!;
            _order.RemoveLast();
            _index.Remove(last.Value);
            evicted.Add(last.Value);
        }

        return evicted;
    }
}

/// <summary>
///     The battery/load and idle policy for the warm service (R40): it must not drain a laptop or fight the
///     foreground, so it pauses pre-warming on battery or under high system load, and evicts a repo idle past a
///     window.
/// </summary>
public static class WarmServicePolicy
{
    /// <summary>Whether pre-warming should pause: on battery, or when the system is under high load.</summary>
    /// <param name="onBattery">Whether the machine is on battery power.</param>
    /// <param name="highLoad">Whether the system is under high CPU load.</param>
    /// <returns><see langword="true" /> when pre-warming should pause.</returns>
    public static bool ShouldPause(bool onBattery, bool highLoad) => onBattery || highLoad;

    /// <summary>Whether a repo idle for <paramref name="idle" /> should be evicted from the warm set.</summary>
    /// <param name="idle">How long the repo has been idle.</param>
    /// <param name="idleWindow">The idle-evict window.</param>
    /// <returns><see langword="true" /> when the repo should be evicted.</returns>
    public static bool ShouldEvict(TimeSpan idle, TimeSpan idleWindow) => idle >= idleWindow;
}

/// <summary>
///     Generates the per-platform service definition and the register/unregister commands for the warm service
///     (R40). The generation is pure and testable; the actual OS registration (which needs privileges and mutates
///     the machine) is performed only by the explicit <c>fuse warm --service install</c> command.
/// </summary>
public static class WarmServiceDefinition
{
    /// <summary>
    ///     The install command a user (or the installer) runs to register the warm service for the running
    ///     platform, keeping a warm daemon alive. Includes the first-run notice describing how to disable it.
    /// </summary>
    /// <param name="fuseInvocation">The absolute invocation of the fuse tool (for example the apphost path).</param>
    /// <returns>The platform-native register command text.</returns>
    public static string InstallCommand(string fuseInvocation)
    {
        if (OperatingSystem.IsWindows())
            return $"New-Service -Name {WarmService.ServiceName} -BinaryPathName '\"{fuseInvocation}\" warm --service run' -StartupType Automatic";
        if (OperatingSystem.IsMacOS())
            return $"launchctl load ~/Library/LaunchAgents/{WarmService.ServiceName}.plist";
        return $"systemctl --user enable --now {WarmService.ServiceName}.service";
    }

    /// <summary>The uninstall command that unregisters the warm service for the running platform (clean uninstall).</summary>
    /// <returns>The platform-native unregister command text.</returns>
    public static string UninstallCommand()
    {
        if (OperatingSystem.IsWindows())
            return $"Remove-Service -Name {WarmService.ServiceName}";
        if (OperatingSystem.IsMacOS())
            return $"launchctl unload ~/Library/LaunchAgents/{WarmService.ServiceName}.plist";
        return $"systemctl --user disable --now {WarmService.ServiceName}.service";
    }

    /// <summary>The one-line first-run notice naming how to disable the service, shown on install.</summary>
    /// <returns>The notice text.</returns>
    public static string FirstRunNotice() =>
        $"Fuse warm service installed (opt-in). It keeps recently-used repos warm, store-backed only, battery-aware. " +
        $"Disable any time with: fuse warm --service uninstall.";

    /// <summary>A short description of the running platform's service mechanism, for the install report.</summary>
    /// <returns>The platform mechanism name.</returns>
    public static string PlatformMechanism() =>
        OperatingSystem.IsWindows() ? "Windows service"
        : OperatingSystem.IsMacOS() ? "launchd agent"
        : "systemd user unit";
}

/// <summary>A runnable command (executable plus arguments) the warm-service installer invokes.</summary>
/// <param name="Executable">The executable to run.</param>
/// <param name="Arguments">The argument string.</param>
public sealed record WarmServiceCommand(string Executable, string Arguments);

/// <summary>The outcome of a warm-service install or uninstall attempt (R40).</summary>
/// <param name="Succeeded">Whether the OS registration/unregistration succeeded.</param>
/// <param name="Message">A human-readable result, including the manual command to run on a failed attempt.</param>
public sealed record WarmServiceActionResult(bool Succeeded, string Message);

/// <summary>
///     Installs and uninstalls the opt-in warm service (R40, option 1): it actually attempts the platform
///     registration (Windows <c>New-Service</c>, launchd <c>launchctl load</c>, systemd <c>systemctl --user
///     enable</c>) and falls back to printing the exact manual command when it cannot (for example no elevation on
///     Windows), rather than only reporting. The command runner is injectable so the attempt-then-fall-back logic
///     is testable without mutating the machine.
/// </summary>
public static class WarmServiceInstaller
{
    /// <summary>
    ///     Attempts to register the warm service, writing the platform unit/plist first where needed, then falling
    ///     back to the manual command on failure.
    /// </summary>
    /// <param name="fuseInvocation">The absolute fuse invocation the service should run.</param>
    /// <param name="runner">The command runner (returns an exit code); defaults to a real process runner.</param>
    /// <returns>The action result.</returns>
    public static WarmServiceActionResult Install(string fuseInvocation, Func<WarmServiceCommand, int>? runner = null)
    {
        runner ??= DefaultRunner;
        try
        {
            WriteUnitFileIfNeeded(fuseInvocation);
            var command = RegisterCommand(fuseInvocation);
            var exit = runner(command);
            if (exit == 0)
                return new WarmServiceActionResult(true, $"warm service installed ({WarmServiceDefinition.PlatformMechanism()}). {WarmServiceDefinition.FirstRunNotice()}");
            return Fallback(exit);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Fallback(reason: ex.Message);
        }
    }

    /// <summary>Attempts to unregister the warm service, falling back to the manual command on failure.</summary>
    /// <param name="runner">The command runner; defaults to a real process runner.</param>
    /// <returns>The action result.</returns>
    public static WarmServiceActionResult Uninstall(Func<WarmServiceCommand, int>? runner = null)
    {
        runner ??= DefaultRunner;
        try
        {
            var exit = runner(UnregisterCommand());
            if (exit == 0)
                return new WarmServiceActionResult(true, $"warm service uninstalled ({WarmServiceDefinition.PlatformMechanism()}).");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new WarmServiceActionResult(false, $"could not uninstall automatically ({ex.Message}); run manually: {WarmServiceDefinition.UninstallCommand()}");
        }

        return new WarmServiceActionResult(false, $"could not uninstall automatically; run manually: {WarmServiceDefinition.UninstallCommand()}");
    }

    private static WarmServiceActionResult Fallback(int? exit = null, string? reason = null)
    {
        var why = reason ?? (exit is not null ? $"exit {exit}" : "unknown");
        return new WarmServiceActionResult(
            false,
            $"could not register the warm service automatically ({why}; this often needs elevation). Run manually: {WarmServiceDefinition.InstallCommand("fuse")}");
    }

    // The runnable register command for the running platform. On Windows New-Service is self-contained; on unix the
    // unit/plist is written by WriteUnitFileIfNeeded and this enables/loads it.
    private static WarmServiceCommand RegisterCommand(string fuseInvocation)
    {
        if (OperatingSystem.IsWindows())
            return new WarmServiceCommand("powershell",
                $"-NoProfile -Command \"New-Service -Name {WarmService.ServiceName} -BinaryPathName '\\\"{fuseInvocation}\\\" warm --service run' -StartupType Automatic\"");
        if (OperatingSystem.IsMacOS())
            return new WarmServiceCommand("launchctl", $"load {PlistPath()}");
        return new WarmServiceCommand("systemctl", $"--user enable --now {WarmService.ServiceName}.service");
    }

    private static WarmServiceCommand UnregisterCommand()
    {
        if (OperatingSystem.IsWindows())
            return new WarmServiceCommand("powershell", $"-NoProfile -Command \"Remove-Service -Name {WarmService.ServiceName}\"");
        if (OperatingSystem.IsMacOS())
            return new WarmServiceCommand("launchctl", $"unload {PlistPath()}");
        return new WarmServiceCommand("systemctl", $"--user disable --now {WarmService.ServiceName}.service");
    }

    // Unix services need a unit/plist file on disk before enable/load; Windows New-Service does not.
    private static void WriteUnitFileIfNeeded(string fuseInvocation)
    {
        if (OperatingSystem.IsWindows())
            return;

        if (OperatingSystem.IsMacOS())
        {
            var plist = PlistPath();
            Directory.CreateDirectory(Path.GetDirectoryName(plist)!);
            File.WriteAllText(plist,
                $"<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>Label</key><string>{WarmService.ServiceName}</string>" +
                $"<key>ProgramArguments</key><array><string>{fuseInvocation}</string><string>warm</string><string>--service</string><string>run</string></array>" +
                "<key>RunAtLoad</key><true/></dict></plist>");
            return;
        }

        var unit = SystemdUnitPath();
        Directory.CreateDirectory(Path.GetDirectoryName(unit)!);
        File.WriteAllText(unit,
            $"[Unit]\nDescription=Fuse warm service\n[Service]\nExecStart={fuseInvocation} warm --service run\nRestart=on-failure\n[Install]\nWantedBy=default.target\n");
    }

    private static string PlistPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", WarmService.ServiceName + ".plist");

    private static string SystemdUnitPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "systemd", "user", WarmService.ServiceName + ".service");

    private static int DefaultRunner(WarmServiceCommand command)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(command.Executable, command.Arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
                return -1;
            process.WaitForExit(TimeSpan.FromSeconds(30));
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }
}

/// <summary>
///     Persists the recently-used repos the warm service re-warms (R40), as a bounded LRU under the user-data
///     directory, so the always-on service knows which repos to keep warm across sessions and reboots.
/// </summary>
public static class WarmServiceState
{
    private static string StatePath() =>
        Path.Combine(FuseStorePaths.GetUserDataDirectory(), "warm-service", "recent.json");

    /// <summary>Records a repo as most-recently-used, bounded by the LRU cap. Best-effort.</summary>
    /// <param name="root">The repo root that was warmed.</param>
    public static void Record(string root)
    {
        try
        {
            var lru = new WarmServiceLru();
            foreach (var existing in Recent())
                lru.Touch(existing);
            lru.Touch(Path.GetFullPath(root));

            var path = StatePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(new WarmServiceRecent(lru.Repos.ToList()), FuseCliJsonContext.Default.WarmServiceRecent);
            File.WriteAllText(path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Returns the recently-used repos, most recent first (empty when none recorded).</summary>
    /// <returns>The recent repo roots.</returns>
    public static IReadOnlyList<string> Recent()
    {
        try
        {
            var path = StatePath();
            if (!File.Exists(path))
                return [];
            var state = JsonSerializer.Deserialize(File.ReadAllText(path), FuseCliJsonContext.Default.WarmServiceRecent);
            return state?.Roots ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}

/// <summary>The persisted recent-repos LRU for the warm service (R40).</summary>
/// <param name="Roots">The recent repo roots, most recent first.</param>
public sealed record WarmServiceRecent(IReadOnlyList<string> Roots);

/// <summary>
///     One tick of the warm-service loop (R40): warms the recent repos when not paused (battery/load), so the
///     always-on service keeps the hot set ready without draining a laptop. Pure and testable.
/// </summary>
public static class WarmServiceRunner
{
    /// <summary>
    ///     Runs one warm tick. When paused, warms nothing; otherwise warms each repo via <paramref name="warmOne" />.
    /// </summary>
    /// <param name="repos">The repos to keep warm.</param>
    /// <param name="paused">Whether pre-warming is paused (battery/load).</param>
    /// <param name="warmOne">Warms one repo.</param>
    /// <param name="cancellationToken">A token to cancel the tick.</param>
    /// <returns>The number of repos warmed this tick (zero when paused).</returns>
    public static async Task<int> RunOnceAsync(
        IReadOnlyList<string> repos, bool paused, Func<string, CancellationToken, Task> warmOne, CancellationToken cancellationToken)
    {
        if (paused)
            return 0;

        var warmed = 0;
        foreach (var repo in repos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await warmOne(repo, cancellationToken);
                warmed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort: one repo failing to warm must not stop the service.
            }
        }

        return warmed;
    }
}

/// <summary>Best-effort power state for the warm service's battery-aware pause (R40).</summary>
public static class PowerState
{
    /// <summary>Whether the machine is currently on battery power (Windows only; false elsewhere/best-effort).</summary>
    /// <returns><see langword="true" /> when on battery.</returns>
    public static bool OnBattery()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            return GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus; // 0 = offline (on battery), 1 = online.
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }
}
