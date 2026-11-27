using System.Text;
using Fuse.Dotnet.Processes;
using Fuse.Protocol.Requests;
namespace Fuse.Operations.Judge;

/// <summary>Reads candidate diffs for the judge from patch files and worktree directories.</summary>
internal static class CandidateDiffLoader
{
    internal static async Task<(JudgeRequest.Candidate? Candidate, string? Refusal)> LoadCandidateAsync(
        string spec,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        string full;
        try
        {
            full = Path.GetFullPath(spec, workingDirectory);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, $"fuse: '{spec}' is not a valid path");
        }

        if (Directory.Exists(full))
        {
            var diffResult = await ProcessRunner.RunAsync("git", ["-C", full, "diff", "--no-color", "HEAD"], full, cancellationToken).ConfigureAwait(false);
            if (diffResult.ExitCode != 0)
                return (null, $"fuse: git diff failed for worktree '{spec}': {diffResult.Output.Trim()}");

            var sb = new StringBuilder(diffResult.Output);
            var untracked = await ProcessRunner.RunAsync("git", ["-C", full, "ls-files", "--others", "--exclude-standard"], full, cancellationToken).ConfigureAwait(false);
            if (untracked.ExitCode == 0)
            {
                foreach (var rel in untracked.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var normalized = rel.Replace('\\', '/');
                    var filePath = Path.Combine(full, rel);
                    if (!File.Exists(filePath))
                        continue;
                    var content = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
                    AppendAddedFileDiff(sb, normalized, content);
                }
            }

            var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return (new JudgeRequest.Candidate(string.IsNullOrEmpty(name) ? spec : name, sb.ToString(), full), null);
        }

        if (File.Exists(full))
        {
            var diff = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
            return (new JudgeRequest.Candidate(Path.GetFileName(full), diff), null);
        }

        return (null, $"fuse: patch or worktree '{spec}' does not exist");
    }

    private static void AppendAddedFileDiff(StringBuilder sb, string relativePath, string content)
    {
        var lines = content.Split('\n');
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        if (sb.Length > 0 && sb[^1] != '\n')
            sb.Append('\n');
        sb.Append($"diff --git a/{relativePath} b/{relativePath}\n");
        sb.Append("new file mode 100644\n");
        sb.Append("--- /dev/null\n");
        sb.Append($"+++ b/{relativePath}\n");
        sb.Append($"@@ -0,0 +1,{count} @@\n");
        for (var i = 0; i < count; i++)
            sb.Append('+').Append(lines[i]).Append('\n');
        if (count > 0 && !content.EndsWith('\n'))
            sb.Append("\\ No newline at end of file\n");
    }
}
