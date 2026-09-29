using System.Text;

namespace Fuse.Protocol;

/// <summary>
///     How a message travels on an engine pipe: one line of UTF-8 JSON, ended by a newline. The client and the engine
///     read with the same code, so neither depends on the other to know where a message ends.
/// </summary>
internal static class PipeFraming
{
    /// <summary>
    ///     Reads one newline-terminated UTF-8 line in 4 KB chunks. Each connection carries exactly one message in each
    ///     direction, so anything after the newline cannot exist and nothing is lost by reading ahead.
    /// </summary>
    /// <returns>The line without its newline, or null when the stream ended before anything was read.</returns>
    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return line.Length == 0 ? null : Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (newline >= 0)
            {
                line.Write(buffer, 0, newline);
                return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
            }

            line.Write(buffer, 0, read);
        }
    }
}
