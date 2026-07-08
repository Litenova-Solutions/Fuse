namespace Fuse.Workspace;

/// <summary>
///     Best-effort extraction of the dotnet command sequence a GitHub Actions workflow runs (G8), plus the steps
///     that cannot be rehearsed locally. Full CI emulation is a tar pit; this is classification, not emulation, so
///     it uses a dependency-free line scan of the workflow YAML (single-line <c>run:</c> and <c>run: |</c> blocks)
///     rather than a full YAML parser, and names what it cannot rehearse rather than letting it surprise. The
///     explicit-command escape hatch (a caller-supplied command list) covers whatever the scan misses.
/// </summary>
/// <remarks>
///     A <c>dotnet</c> command that references a secret (<c>${{ secrets. }}</c> or <c>--api-key</c>) or pushes a
///     package (<c>nuget push</c>) is classified non-rehearsable rather than run, because it needs credentials or
///     mutates a remote. Everything else that starts with <c>dotnet</c> (build, test, restore, pack, format, tool)
///     is a rehearsable step, in source order.
/// </remarks>
public static class CiWorkflowParser
{
    /// <summary>
    ///     Extracts the rehearsable dotnet commands and the non-rehearsable step notes from a workflow file's text.
    /// </summary>
    /// <param name="workflowText">The workflow YAML content.</param>
    /// <returns>The parse result: the ordered rehearsable dotnet commands and the classified non-rehearsable steps.</returns>
    public static CiWorkflowParse Parse(string workflowText)
    {
        var rehearsable = new List<string>();
        var nonRehearsable = new List<string>();
        if (string.IsNullOrEmpty(workflowText))
            return new CiWorkflowParse(rehearsable, nonRehearsable);

        var lines = workflowText.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            // A step's command shell: "run: <cmd>" (single line) or "run: |" (a block of following, more-indented
            // lines). The leading "- " of a list item is stripped so "- run:" is recognized too.
            var runIndex = RunDirectiveIndex(trimmed);
            if (runIndex < 0)
                continue;

            var after = trimmed[(runIndex + 4)..].Trim();
            if (after is "|" or ">" or "|-" or ">-" or "|+" or ">+")
            {
                // A block scalar: consume the following lines that are indented deeper than the run: key.
                var baseIndent = IndentOf(line);
                for (var j = i + 1; j < lines.Length; j++)
                {
                    if (lines[j].Trim().Length == 0)
                        continue;
                    if (IndentOf(lines[j]) <= baseIndent)
                        break;
                    ClassifyCommand(lines[j].Trim(), rehearsable, nonRehearsable);
                    i = j;
                }
            }
            else if (after.Length > 0)
            {
                ClassifyCommand(after, rehearsable, nonRehearsable);
            }
        }

        return new CiWorkflowParse(rehearsable, nonRehearsable);
    }

    // The character index just before the "run:" token in a step line, or -1. Handles a leading "- " list marker.
    private static int RunDirectiveIndex(string trimmed)
    {
        var s = trimmed.StartsWith("- ", StringComparison.Ordinal) ? trimmed[2..].TrimStart() : trimmed;
        // Recompute the offset the "run:" sits at within the original trimmed string.
        if (!s.StartsWith("run:", StringComparison.Ordinal))
            return -1;
        return trimmed.IndexOf("run:", StringComparison.Ordinal);
    }

    private static int IndentOf(string line)
    {
        var n = 0;
        while (n < line.Length && line[n] == ' ')
            n++;
        return n;
    }

    // Classifies one command line: a dotnet command is rehearsable unless it needs a secret or pushes a package,
    // in which case it is recorded as a named non-rehearsable step; a non-dotnet command is ignored (out of scope).
    private static void ClassifyCommand(string command, List<string> rehearsable, List<string> nonRehearsable)
    {
        if (!ContainsDotnetInvocation(command))
            return;

        if (command.Contains("secrets.", StringComparison.OrdinalIgnoreCase) || command.Contains("--api-key", StringComparison.OrdinalIgnoreCase))
        {
            nonRehearsable.Add($"needs a secret: {command}");
            return;
        }

        if (command.Contains("nuget push", StringComparison.OrdinalIgnoreCase) || command.Contains("nuget", StringComparison.OrdinalIgnoreCase) && command.Contains("push", StringComparison.OrdinalIgnoreCase))
        {
            nonRehearsable.Add($"publishes a package: {command}");
            return;
        }

        rehearsable.Add(command);
    }

    // A dotnet CLI invocation: the line starts with "dotnet " or contains a "dotnet " token not inside a longer
    // word. Covers "dotnet build", a leading env prefix, and a "dotnet test" embedded in a coverage wrapper.
    private static bool ContainsDotnetInvocation(string command)
    {
        var idx = command.IndexOf("dotnet ", StringComparison.Ordinal);
        while (idx >= 0)
        {
            var precededByWordChar = idx > 0 && (char.IsLetterOrDigit(command[idx - 1]) || command[idx - 1] is '-' or '_');
            if (!precededByWordChar)
                return true;
            idx = command.IndexOf("dotnet ", idx + 1, StringComparison.Ordinal);
        }

        return false;
    }
}

/// <summary>The result of parsing a CI workflow for its dotnet steps (G8).</summary>
/// <param name="RehearsableCommands">The dotnet commands that can be run locally, in source order.</param>
/// <param name="NonRehearsableSteps">The dotnet steps that cannot be rehearsed locally, each with its named reason.</param>
public sealed record CiWorkflowParse(
    IReadOnlyList<string> RehearsableCommands,
    IReadOnlyList<string> NonRehearsableSteps);
