using System.Text;
using Fuse.Paths;

namespace Fuse.Harnesses;

/// <summary>
///     The Fuse section <c>fuse init --instructions</c> writes into the instruction files agents read: <c>AGENTS.md</c>,
///     created when missing, and <c>CLAUDE.md</c> and <c>GEMINI.md</c> when they exist. The section sits between
///     <see cref="Begin"/> and <see cref="End"/>; a later run replaces only what is between them and leaves the rest of
///     the file as it is.
/// </summary>
/// <remarks>
///     Claude Code reads <c>CLAUDE.md</c>, and reads <c>AGENTS.md</c> only when there is no <c>CLAUDE.md</c>, so a
///     <c>CLAUDE.md</c> that imports <c>@AGENTS.md</c> already has the section and gets no second copy. Gemini CLI reads
///     <c>GEMINI.md</c> by default.
/// </remarks>
internal static class InstructionsFile
{
    /// <summary>The line that opens Fuse's section.</summary>
    public const string Begin = "<!-- fuse:begin -->";

    /// <summary>The line that closes Fuse's section.</summary>
    public const string End = "<!-- fuse:end -->";

    /// <summary>What the section tells an agent, between the markers.</summary>
    internal const string Section = """
        ## Fuse

        This repository uses [Fuse](https://fuse.codes) to check .NET changes. Its hooks run on their own; these commands are for when you need them.

        - After each edit, Fuse tells you the compiler errors that edit introduced. Fix them before you go on.
        - Run `fuse test` to run the tests your changes can break; `fuse test --all` runs every test. `dotnet test` is rewritten to `fuse test`.
        - Run `fuse build` instead of `dotnet build`: it prints only the errors, and waits for another agent's build instead of colliding with it.
        - When you finish, Fuse checks every change since HEAD (the commit checked out) and sends you back for the errors your edits introduced. `fuse check` runs the same check when you want it sooner.
        - When several agents work in this working tree, each is told the errors its own edits cause, and `fuse test` runs the tests its own edits affect.
        - If Fuse stops answering, `fuse stop` stops its engine; the next command starts a new one.
        """;

    /// <summary>The instruction files, relative to the root, that get the section.</summary>
    public static List<string> FilesFor(RepoRoot root)
    {
        var files = new List<string> { "AGENTS.md" };
        var claude = Path.Combine(root.Path, "CLAUDE.md");
        if (File.Exists(claude) && !File.ReadAllText(claude).Contains("@AGENTS.md", StringComparison.Ordinal))
            files.Add("CLAUDE.md");
        if (File.Exists(Path.Combine(root.Path, "GEMINI.md")))
            files.Add("GEMINI.md");
        return files;
    }

    /// <summary>
    ///     Puts the section in <paramref name="relative"/>: between the markers when the file has them, at its end when it
    ///     has none, or as the whole file when it does not exist. The file's own line endings are kept.
    /// </summary>
    internal static WrittenFile Write(RepoRoot root, string relative)
    {
        var path = Path.Combine(root.Path, relative);
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        var newline = existing is not null && existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        // The section's own line endings follow the checkout of this source file, so they are normalized first.
        var block = string.Join(newline, [Begin, .. Section.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'), End]);
        string content;
        if (existing is null)
        {
            content = block + newline;
        }
        else
        {
            var start = existing.IndexOf(Begin, StringComparison.Ordinal);
            var end = start < 0 ? -1 : existing.IndexOf(End, start, StringComparison.Ordinal);
            if (start >= 0 && end >= 0)
            {
                content = existing[..start] + block + existing[(end + End.Length)..];
            }
            else
            {
                var text = new StringBuilder(existing.TrimEnd('\r', '\n'));
                if (text.Length > 0)
                    text.Append(newline).Append(newline);
                content = text.Append(block).Append(newline).ToString();
            }
        }

        return SettingsFile.WriteText(root, path, content);
    }

    /// <summary>The instruction files, relative to the root, that <c>fuse uninit</c> looks for the section in.</summary>
    public static readonly IReadOnlyList<string> AllFiles = ["AGENTS.md", "CLAUDE.md", "GEMINI.md"];

    /// <summary>
    ///     Takes the section, markers included, out of <paramref name="relative"/>, with the blank lines <see cref="Write"/>
    ///     put before it, and keeps the rest of the file. A file left with nothing but whitespace is deleted, as the one
    ///     <see cref="Write"/> created. Returns the file, or null when it has no section.
    /// </summary>
    internal static WrittenFile? Remove(RepoRoot root, string relative)
    {
        var path = Path.Combine(root.Path, relative);
        if (!File.Exists(path))
            return null;
        var existing = File.ReadAllText(path);
        var start = existing.IndexOf(Begin, StringComparison.Ordinal);
        var end = start < 0 ? -1 : existing.IndexOf(End, start, StringComparison.Ordinal);
        if (start < 0 || end < 0)
            return null;

        var newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var before = existing[..start].TrimEnd('\r', '\n');
        var after = existing[(end + End.Length)..].TrimStart('\r', '\n');
        var content = before.Length == 0 ? after : after.Length == 0 ? before + newline : before + newline + newline + after;
        return string.IsNullOrWhiteSpace(content) ? SettingsFile.Delete(root, path) : SettingsFile.WriteText(root, path, content);
    }
}
