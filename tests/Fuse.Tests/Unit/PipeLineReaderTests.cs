using System.Text;
using Fuse.Protocol;

namespace Fuse.Tests.Unit;

/// <summary>Reading lines from a stream whose messages arrive back to back, as a watch stream's do.</summary>
public class PipeLineReaderTests
{
    [Fact]
    public async Task Two_lines_in_one_read_are_both_returned()
    {
        var reader = new PipeLineReader(new MemoryStream(Encoding.UTF8.GetBytes("{\"a\":1}\n{\"b\":2}\n")));

        Assert.Equal("{\"a\":1}", await reader.ReadLineAsync(TestContext.Current.CancellationToken));
        Assert.Equal("{\"b\":2}", await reader.ReadLineAsync(TestContext.Current.CancellationToken));
        Assert.Null(await reader.ReadLineAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_line_split_across_reads_is_joined()
    {
        var reader = new PipeLineReader(new ChunkedStream(Encoding.UTF8.GetBytes("first line\nsecond"), chunk: 3));

        Assert.Equal("first line", await reader.ReadLineAsync(TestContext.Current.CancellationToken));
        Assert.Equal("second", await reader.ReadLineAsync(TestContext.Current.CancellationToken));
        Assert.Null(await reader.ReadLineAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_line_longer_than_the_buffer_is_read_whole()
    {
        var text = new string('x', 10_000);
        var reader = new PipeLineReader(new MemoryStream(Encoding.UTF8.GetBytes(text + "\nnext\n")));

        Assert.Equal(text, await reader.ReadLineAsync(TestContext.Current.CancellationToken));
        Assert.Equal("next", await reader.ReadLineAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A read-only stream that returns at most <c>chunk</c> bytes per read, as a pipe can.</summary>
    private sealed class ChunkedStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
    }
}
