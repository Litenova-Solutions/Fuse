using System.Text;
using System.Text.Json.Nodes;
using Fuse.Hooks;

namespace Fuse.Tests.Unit;

/// <summary>
///     The hook reads its payload as UTF-8, not through the console. A payload whose path holds a non-ASCII character is
///     what breaks otherwise: on a Windows console the code page is not UTF-8, the bytes decode into different characters,
///     and the JSON no longer parses, so the hook reports nothing and the agent is told its edit is clean.
/// </summary>
public class HookStdinTests
{
    private static readonly string Repo = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";

    private static string PayloadWith(string file) => new JsonObject
    {
        ["hook_event_name"] = "PostToolUse",
        ["tool_name"] = "Edit",
        ["cwd"] = Repo,
        ["tool_input"] = new JsonObject { ["file_path"] = file },
    }.ToJsonString();

    private static async Task<string> ReadAsUtf8Async(string text)
    {
        using var bytes = new MemoryStream(new UTF8Encoding(false).GetBytes(text));
        return await HookCommand.ReadUtf8Async(bytes, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_payload_with_a_non_ascii_path_round_trips()
    {
        var file = Path.Combine(Repo, "src", "blåbærgrød", "Kasse.cs");
        var read = await ReadAsUtf8Async(PayloadWith(file));
        Assert.Equal([file], HookPayload.Parse(read).EditedFiles());
    }

    [Fact]
    public async Task A_payload_with_a_non_ascii_directory_round_trips()
    {
        var file = Path.Combine(Repo, "ÆøÅ", "q.cs");
        var read = await ReadAsUtf8Async(PayloadWith(file));
        Assert.Equal([file], HookPayload.Parse(read).EditedFiles());
    }

    [Fact]
    public async Task The_bytes_are_read_as_utf8_even_when_the_console_says_otherwise()
    {
        // The console's decoding is the thing that was wrong. Reading a known UTF-8 byte sequence must not depend on it.
        using var latin = new MemoryStream([0x7B, 0x22, 0x61, 0x22, 0x3A, 0x22, 0xC3, 0xB8, 0x22, 0x7D]);
        var read = await HookCommand.ReadUtf8Async(latin, TestContext.Current.CancellationToken);
        Assert.Equal("""{"a":"ø"}""", read);
    }

    [Fact]
    public async Task A_byte_order_mark_is_read_as_a_character_not_as_an_encoding()
    {
        // Every harness writes UTF-8 without a mark. A mark is tolerated as content rather than silently dropped, so a
        // payload that begins with one is a harness problem the parse failure reports.
        using var marked = new MemoryStream(new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes("{}")).ToArray());
        var read = await HookCommand.ReadUtf8Async(marked, TestContext.Current.CancellationToken);
        Assert.Equal("﻿{}", read);
    }

    [Fact]
    public async Task An_empty_payload_reads_as_empty()
    {
        Assert.Equal("", await ReadAsUtf8Async(""));
    }
}
