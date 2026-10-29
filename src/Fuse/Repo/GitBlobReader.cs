using System.Diagnostics;
using System.Text;
using Fuse.Failures;

namespace Fuse.Repo;

/// <summary>
///     Reads file contents at a commit through one long-lived <c>git cat-file --batch</c> process, so reading a
///     baseline file costs a pipe round trip instead of a process start.
/// </summary>
internal sealed class GitBlobReader : IDisposable
{
    private readonly string _root;
    private readonly Lock _processLock = new();
    private Process? _process;

    public GitBlobReader(string root) => _root = root;

    /// <summary>Returns the bytes of <paramref name="relativePath"/> at <paramref name="commit"/>, or null when the file is not in that commit.</summary>
    public byte[]? Read(string commit, string relativePath)
    {
        lock (_processLock)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return ReadCore(commit, relativePath);
                }
                catch (IOException) when (attempt == 0)
                {
                    // The git process exited (for example because git was replaced on disk); start it once more.
                    Stop();
                }
                catch (IOException)
                {
                    Stop();
                    throw new FuseException(ErrorCode.LoadFailed, $"git cannot read {relativePath} at HEAD; the repository may be damaged (run `git fsck`)");
                }
            }
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
        if (parts.Length != 3 || !int.TryParse(parts[2], out var size))
        {
            // A header that does not parse leaves the stream at an unknown place, so the next read starts a new process.
            Stop();
            return null;
        }

        // Git sends the content of any object the path names, a directory's tree included. It is read either way, so
        // the next header is where the next read looks for it.
        var content = new byte[size];
        stdout.ReadExactly(content);
        stdout.ReadByte(); // The trailing newline after the content.
        return parts[1] == "blob" ? content : null;
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
        lock (_processLock)
            Stop();
    }
}
