using System.Diagnostics;
using System.Text;

namespace Fuse.Repo;

/// <summary>
///     Reads file contents at a commit through one long-lived <c>git cat-file --batch</c> process, so reading a
///     baseline file costs a pipe round trip instead of a process start.
/// </summary>
internal sealed class GitBlobReader : IDisposable
{
    private readonly string _root;
    private readonly Lock _gate = new();
    private Process? _process;

    public GitBlobReader(string root) => _root = root;

    /// <summary>Returns the bytes of <paramref name="relativePath"/> at <paramref name="commit"/>, or null when the file does not exist there.</summary>
    public byte[]? Read(string commit, string relativePath)
    {
        lock (_gate)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    return ReadCore(commit, relativePath);
                }
                catch (IOException)
                {
                    // The process died (for example git was upgraded underneath); restart once.
                    Stop();
                }
            }

            return null;
        }
    }

    private byte[]? ReadCore(string commit, string relativePath)
    {
        var process = _process ??= Start();
        var stdin = process.StandardInput.BaseStream;
        var request = Encoding.UTF8.GetBytes($"{commit}:{relativePath}\n");
        stdin.Write(request);
        stdin.Flush();

        var stdout = process.StandardOutput.BaseStream;
        var header = ReadLine(stdout);
        // "<id> blob <size>" or "<spec> missing".
        if (header.EndsWith(" missing", StringComparison.Ordinal) || header.EndsWith(" ambiguous", StringComparison.Ordinal))
            return null;
        var parts = header.Split(' ');
        if (parts.Length != 3 || parts[1] != "blob" || !int.TryParse(parts[2], out var size))
            return null;
        var content = new byte[size];
        stdout.ReadExactly(content);
        stdout.ReadByte(); // The trailing newline after the content.
        return content;
    }

    private static string ReadLine(Stream stream)
    {
        var bytes = new List<byte>(64);
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0)
                throw new IOException("git cat-file ended");
            if (b == '\n')
                break;
            bytes.Add((byte)b);
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private Process Start()
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("cat-file");
        psi.ArgumentList.Add("--batch");
        var process = Process.Start(psi) ?? throw new IOException("could not start git");
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        return process;
    }

    private void Stop()
    {
        try
        {
            _process?.Kill();
        }
        catch (InvalidOperationException)
        {
        }

        _process?.Dispose();
        _process = null;
    }

    public void Dispose()
    {
        lock (_gate)
            Stop();
    }
}
