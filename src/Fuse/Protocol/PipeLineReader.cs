using System.Text;

namespace Fuse.Protocol;

/// <summary>
///     Reads newline-terminated UTF-8 lines from a stream that carries several messages back to back, such as a watch
///     stream. Bytes read past a newline are kept for the next line, so two lines that arrive in one read are both
///     returned.
/// </summary>
internal sealed class PipeLineReader
{
    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[4096];
    private int _start;
    private int _end;

    /// <param name="stream">The stream to read lines from.</param>
    public PipeLineReader(Stream stream) => _stream = stream;

    /// <summary>Reads the next line.</summary>
    /// <returns>The line without its newline, or null when the stream ended before anything of a line was read.</returns>
    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        while (true)
        {
            if (_start < _end)
            {
                var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                if (newline >= 0)
                {
                    line.Write(_buffer, _start, newline - _start);
                    _start = newline + 1;
                    return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
                }

                line.Write(_buffer, _start, _end - _start);
            }

            _start = 0;
            _end = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            if (_end == 0)
                return line.Length == 0 ? null : Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
        }
    }
}
