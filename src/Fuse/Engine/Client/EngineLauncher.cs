using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Fuse.Paths;
using Fuse.Protocol;

namespace Fuse.Engine.Client;

/// <summary>
///     Starts <c>fuse engine</c> fully detached from the caller. The engine outlives the hook or command that
///     started it, so it must not hold any of the caller's handles (an inherited stdout keeps a hook's pipe open and
///     hangs the harness) and must survive the harness killing the caller's process group or job.
/// </summary>
internal static class EngineLauncher
{
    /// <summary>Starts an engine for <paramref name="root"/>. A redundant start is harmless: the second engine fails to take the root's mutex and exits.</summary>
    public static void Start(string root)
    {
        var (fileName, arguments) = Command(root);
        if (OperatingSystem.IsWindows())
            StartWindows(fileName, arguments, root);
        else
            StartUnix(fileName, arguments, root);
    }

    private static (string FileName, List<string> Arguments) Command(string root)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("cannot locate the fuse executable");
        var home = EngineHome();
        var arguments = new List<string>();
        // A dotnet tool package carries fuse.dll but no managed fuse.exe (its shim lives elsewhere), so the engine runs
        // through the dotnet host unless this build has its own apphost next to fuse.dll. The native client's fuse.exe
        // is the native client itself, which cannot run the engine.
        var appHost = Path.Combine(home, OperatingSystem.IsWindows() ? "fuse.exe" : "fuse");
        string fileName;
        if (!EngineVersion.IsNativeClient && File.Exists(appHost) && !Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            fileName = appHost;
        }
        else
        {
            fileName = DotnetHost(processPath);
            arguments.Add(Path.Combine(home, EngineVersion.ManagedAssembly));
        }

        arguments.Add("engine");
        arguments.Add(root);
        return (fileName, arguments);
    }

    /// <summary>
    ///     The dotnet host executable: this process when it is the host, else the one the environment names, else the one
    ///     next to the running runtime, else the one on the PATH. The native client has no runtime directory to look in.
    /// </summary>
    private static string DotnetHost(string processPath)
    {
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return processPath;
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var candidate in new[] { Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"), Environment.GetEnvironmentVariable("DOTNET_ROOT") is { } dotnetRoot ? Path.Combine(dotnetRoot, name) : null })
        {
            if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                return candidate;
        }

        if (!EngineVersion.IsNativeClient)
        {
            // The runtime directory is <dotnet root>/shared/Microsoft.NETCore.App/<version>/.
            var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            var host = Path.Combine(Path.GetFullPath(Path.Combine(runtime, "..", "..", "..")), name);
            if (File.Exists(host))
                return host;
        }

        return name;
    }

    /// <summary>
    ///     A private copy of this build's binaries that the engine runs from. An engine runs until it has gone without a
    ///     request long enough to exit, which can be hours after it started; running it from the installed tool directory would lock those files and make <c>dotnet tool update</c> (and
    ///     rebuilding Fuse) fail while any repository has an engine running.
    /// </summary>
    internal static string EngineHome() =>
        EngineHome(LocalState.EngineCopies, EngineVersion.Build, () => EngineVersion.ManagedDirectory
            ?? throw new InvalidOperationException($"this build of Fuse has no {EngineVersion.ManagedAssembly} to run the engine from; reinstall Fuse with dotnet tool update -g Fuse"));

    /// <summary>
    ///     The copy of build <paramref name="build"/> under <paramref name="copies"/>, made from <paramref name="source"/>
    ///     when it has no complete copy yet. A copy is complete once its <c>.complete</c> marker exists, which is written
    ///     last. A copy directory without the marker, such as one a cleanup deleted part of, is replaced, and when it cannot
    ///     be replaced because something still holds a file in it, the engine runs from the fresh copy beside it.
    /// </summary>
    internal static string EngineHome(string copies, string build, Func<string> source)
    {
        var home = Path.Combine(copies, build.Replace('/', '-'));
        var marker = Path.Combine(home, ".complete");
        if (File.Exists(marker))
            return home;

        var staging = home + "." + Guid.NewGuid().ToString("N")[..8];
        CopyDirectory(source(), staging);
        File.WriteAllText(Path.Combine(staging, ".complete"), build);
        if (TryMove(staging, home))
            return home;

        if (!File.Exists(marker))
        {
            try
            {
                Directory.Delete(home, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return staging;
            }

            if (TryMove(staging, home))
                return home;
            if (!File.Exists(marker))
                return staging;
        }

        // Another client copied the same build first; use theirs.
        Directory.Delete(staging, recursive: true);
        return home;
    }

    private static bool TryMove(string source, string target)
    {
        try
        {
            Directory.Move(source, target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }

        if (!OperatingSystem.IsWindows())
        {
            var executable = Path.Combine(target, "fuse");
            if (File.Exists(executable))
                File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    private const long MinHeapHardLimitBytes = 768L * 1024 * 1024;
    private const long MaxHeapHardLimitBytes = 2L * 1024 * 1024 * 1024;
    private const long FallbackHeapHardLimitBytes = 1024L * 1024 * 1024;

    private static void StartUnix(string fileName, List<string> arguments, string root)
    {
        // Redirecting all three streams gives the child fresh pipes instead of the caller's descriptors; .NET opens
        // every other descriptor close-on-exec. The engine calls setsid() at startup to leave the caller's session.
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var (key, value) in GcEnvironmentOverrides(Environment.GetEnvironmentVariable, ReadPhysicalMemoryBytes()))
            psi.Environment[key] = value;
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi);
        process?.StandardInput.Close();
    }

    private static void StartWindows(string fileName, List<string> arguments, string root)
    {
        // CreateProcess directly: Process.Start always passes bInheritHandles=TRUE, which leaks the caller's
        // inheritable standard handles into the engine. DETACHED_PROCESS gives it no console; breaking away from
        // the caller's job keeps a harness that kills its hook job from killing the engine.
        const uint detachedProcess = 0x00000008;
        const uint createNewProcessGroup = 0x00000200;
        const uint createBreakawayFromJob = 0x01000000;
        const uint createUnicodeEnvironment = 0x00000400;
        var commandLine = new StringBuilder(Quote(fileName));
        foreach (var argument in arguments)
            commandLine.Append(' ').Append(Quote(argument));

        var environmentBlock = BuildCurrentWindowsEnvironmentBlock();
        var flags = detachedProcess | createNewProcessGroup | createUnicodeEnvironment;
        if (!TryCreate(commandLine.ToString(), flags | createBreakawayFromJob, root, environmentBlock)
            && !TryCreate(commandLine.ToString(), flags, root, environmentBlock))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static bool TryCreate(string commandLine, uint flags, string directory, char[] environmentBlock)
    {
        var startup = new Native.StartupInfo { cb = Marshal.SizeOf<Native.StartupInfo>() };
        // CreateProcessW may write to the command-line buffer, so it gets a private, null-terminated copy.
        var buffer = (commandLine + "\0").ToCharArray();
        var envHandle = GCHandle.Alloc(environmentBlock, GCHandleType.Pinned);
        try
        {
            if (!Native.CreateProcessW(null, buffer, 0, 0, false, flags, envHandle.AddrOfPinnedObject(), directory, ref startup, out var info))
                return false;
            Native.CloseHandle(info.hProcess);
            Native.CloseHandle(info.hThread);
            return true;
        }
        finally
        {
            envHandle.Free();
        }
    }

    /// <summary>
    ///     Computes the engine heap hard limit in bytes: 25 percent of physical RAM clamped between 768 MB and 2 GB,
    ///     falling back to 1 GB when physical RAM cannot be read.
    /// </summary>
    internal static long ComputeHeapHardLimitBytes(long totalPhysicalBytes)
    {
        if (totalPhysicalBytes <= 0)
            return FallbackHeapHardLimitBytes;
        return Math.Clamp(totalPhysicalBytes / 4, MinHeapHardLimitBytes, MaxHeapHardLimitBytes);
    }

    /// <summary>
    ///     The GC tuning variables to apply to the launched engine when not already set in the caller's environment:
    ///     aggressive memory conservation, DATAS dynamic adaptation, and a bounded heap hard limit.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> GcEnvironmentOverrides(Func<string, string?> getVariable, long totalPhysicalBytes)
    {
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        if (IsUnset(getVariable, "DOTNET_GCConserveMemory", "COMPlus_GCConserveMemory"))
            overrides["DOTNET_GCConserveMemory"] = "9";
        if (IsUnset(getVariable, "DOTNET_GCDynamicAdaptationMode", "COMPlus_GCDynamicAdaptationMode"))
            overrides["DOTNET_GCDynamicAdaptationMode"] = "1";
        if (IsUnset(getVariable, "DOTNET_GCHeapHardLimit", "COMPlus_GCHeapHardLimit"))
            overrides["DOTNET_GCHeapHardLimit"] = "0x" + ComputeHeapHardLimitBytes(totalPhysicalBytes).ToString("x", CultureInfo.InvariantCulture);
        return overrides;
    }

    private static bool IsUnset(Func<string, string?> getVariable, string primaryName, string legacyName) =>
        string.IsNullOrWhiteSpace(getVariable(primaryName)) && string.IsNullOrWhiteSpace(getVariable(legacyName));

    private static char[] BuildCurrentWindowsEnvironmentBlock()
    {
        var entries = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && key.Length > 0 && entry.Value is string value)
                entries[key] = value;
        }

        foreach (var (key, value) in GcEnvironmentOverrides(k => entries.GetValueOrDefault(k), ReadPhysicalMemoryBytes()))
            entries[key] = value;

        return BuildWindowsEnvironmentBlock(entries);
    }

    /// <summary>Formats sorted <c>KEY=VALUE\0...\0\0</c> UTF-16 entries for <c>CreateProcessW</c>.</summary>
    internal static char[] BuildWindowsEnvironmentBlock(IEnumerable<KeyValuePair<string, string>> entries)
    {
        var sorted = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in entries)
        {
            if (!string.IsNullOrEmpty(key) && key.IndexOf('\0') < 0 && value.IndexOf('\0') < 0)
                sorted[key] = value;
        }

        var builder = new StringBuilder();
        foreach (var (key, value) in sorted)
            builder.Append(key).Append('=').Append(value).Append('\0');
        if (builder.Length == 0)
            builder.Append('\0');
        builder.Append('\0');
        var chars = new char[builder.Length];
        builder.CopyTo(0, chars, 0, builder.Length);
        return chars;
    }

    internal static long ReadPhysicalMemoryBytes()
    {
        if (OperatingSystem.IsWindows() && ReadWindowsPhysicalMemoryBytes() is var winBytes && winBytes > 0)
            return winBytes;
        if (OperatingSystem.IsLinux() && ReadLinuxPhysicalMemoryBytes() is var linuxBytes && linuxBytes > 0)
            return linuxBytes;

        var info = GC.GetGCMemoryInfo();
        if (info.HighMemoryLoadThresholdBytes > info.TotalAvailableMemoryBytes)
            return (long)(info.HighMemoryLoadThresholdBytes / 0.9);
        return Math.Max(0, info.TotalAvailableMemoryBytes);
    }

    private static long ReadWindowsPhysicalMemoryBytes()
    {
        try
        {
            var status = new Native.PhysicalMemoryStatus { dwLength = (uint)Marshal.SizeOf<Native.PhysicalMemoryStatus>() };
            if (Native.GlobalMemoryStatusEx(ref status) && status.ullTotalPhys > 0)
                return (long)Math.Min(status.ullTotalPhys, (ulong)long.MaxValue);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return 0;
    }

    private static long ReadLinuxPhysicalMemoryBytes()
    {
        try
        {
            if (File.Exists("/proc/meminfo"))
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (!line.StartsWith("MemTotal:", StringComparison.Ordinal))
                        continue;
                    var colon = line.IndexOf(':');
                    if (colon >= 0)
                    {
                        var rest = line.AsSpan(colon + 1).Trim();
                        var space = rest.IndexOf(' ');
                        var number = space >= 0 ? rest[..space] : rest;
                        if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb) && kb > 0)
                            return kb * 1024;
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return 0;
    }

    /// <summary>Quotes one argument by the rules <c>CommandLineToArgvW</c> and the C runtime use.</summary>
    internal static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
            return argument;
        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            builder.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            builder.Append(c);
        }

        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct StartupInfo
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public nint lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessInformation
        {
            public nint hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PhysicalMemoryStatus
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcessW(
            string? applicationName, char[] commandLine, nint processAttributes, nint threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment,
            string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(nint handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref PhysicalMemoryStatus lpBuffer);
    }
}
