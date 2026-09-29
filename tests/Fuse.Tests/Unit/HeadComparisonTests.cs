using System.Text;
using Fuse.Repo;

namespace Fuse.Tests.Unit;

/// <summary>
///     Whether a file differs from HEAD decides whether the tracker counts it as changed. A checkout that converts line
///     endings must not make a file look changed, and a file that exists on one side only always is.
/// </summary>
public class HeadComparisonTests
{
    [Fact]
    public void Line_endings_do_not_count()
    {
        Assert.False(HeadComparison.Differs(Bytes("a\nb\n"), Bytes("a\r\nb\r\n")));
        Assert.False(HeadComparison.Differs(Bytes("a\r\nb\r\n"), Bytes("a\nb\n")));
    }

    [Fact]
    public void A_carriage_return_anywhere_is_skipped()
    {
        // Every carriage return is skipped, not only one before a line feed, so a lone one is no change either.
        Assert.False(HeadComparison.Differs(Bytes("ab"), Bytes("a\rb")));
        Assert.False(HeadComparison.Differs(Bytes("ab\r"), Bytes("ab")));
    }

    [Fact]
    public void Identical_content_does_not_differ() =>
        Assert.False(HeadComparison.Differs(Bytes("same\n"), Bytes("same\n")));

    [Fact]
    public void Different_content_differs_even_when_one_side_is_a_prefix_of_the_other()
    {
        Assert.True(HeadComparison.Differs(Bytes("a\nb\n"), Bytes("a\nc\n")));
        Assert.True(HeadComparison.Differs(Bytes("a\n"), Bytes("a\nb\n")));
        Assert.True(HeadComparison.Differs(Bytes("a\nb\n"), Bytes("a\n")));
    }

    [Fact]
    public void A_file_on_one_side_only_differs_even_when_it_is_empty()
    {
        Assert.True(HeadComparison.Differs(null, Bytes("new\n")));
        Assert.True(HeadComparison.Differs(Bytes("deleted\n"), null));
        Assert.True(HeadComparison.Differs(null, []));
        Assert.True(HeadComparison.Differs([], null));
    }

    [Fact]
    public void A_file_on_neither_side_does_not_differ() =>
        Assert.False(HeadComparison.Differs(null, null));

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
