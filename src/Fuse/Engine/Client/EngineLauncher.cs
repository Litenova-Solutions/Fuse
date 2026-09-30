using System.ComponentModel;
using System.Diagnostics;
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
    ///     A private copy of this build's binaries that the engine runs from. An engine runs until it has had no request
    ///     for 30 minutes, which can be hours after it started; running it from the installed tool directory would lock those files and make <c>dotnet tool update</c> (and
    ///     rebuilding Fuse) fail while any repository has an engine running.
    /// </summary>
    internal static string EngineHome()
    {
        var home = Path.Combine(LocalState.EngineCopies, EngineVersion.Build.Replace('/', '-'));
        var marker = Path.Combine(home, ".complete");
        if (File.Exists(marker))
            return home;

        var source = EngineVersion.ManagedDirectory
                     ?? throw new InvalidOperationException($"this build of Fuse has no {EngineVersion.ManagedAssembly} to run the engine from; reinstall Fuse with dotnet tool update -g Fuse");
        var staging = home + "." + Guid.NewGuid().ToString("N")[..8];
        CopyDirectory(source, staging);
        File.WriteAllText(Path.Combine(staging, ".complete"), EngineVersion.Build);
        try
        {
            Directory.Move(staging, home);
        }
        catch (IOException)
        {
            // Another client copied the same build first; use theirs.
            Directory.Delete(staging, recursive: true);
        }

        return home;
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

        var flags = detachedProcess | createNewProcessGroup | createUnicodeEnvironment;
        if (!TryCreate(commandLine.ToString(), flags | createBreakawayFromJob, root)
            && !TryCreate(commandLine.ToString(), flags, root))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static bool TryCreate(string commandLine, uint flags, string directory)
    {
        var startup = new Native.StartupInfo { cb = Marshal.SizeOf<Native.StartupInfo>() };
        // CreateProcessW may write to the command-line buffer, so it gets a private, null-terminated copy.
        var buffer = (commandLine + "\0").ToCharArray();
        if (!Native.CreateProcessW(null, buffer, 0, 0, false, flags, 0, directory, ref startup, out var info))
            return false;
        Native.CloseHandle(info.hProcess);
        Native.CloseHandle(info.hThread);
        return true;
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcessW(
            string? applicationName, char[] commandLine, nint processAttributes, nint threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment,
            string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(nint handle);
    }
}
