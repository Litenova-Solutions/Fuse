using Fuse.Dotnet;
using Fuse.Failures;
using Fuse.Paths;

namespace Fuse.Repo;

/// <summary>
///     Finds the commit HEAD points at and makes sure git can read it. The ref files, loose or packed, answer almost
///     always; git answers for the rest (reftable refs, unusual layouts).
/// </summary>
internal sealed class HeadResolver
{
    private readonly RepoRoot _root;
    private readonly GitHead _gitHead;

    public HeadResolver(RepoRoot root)
    {
        _root = root;
        _gitHead = new GitHead(root.Path);
    }

    /// <summary>The commit HEAD points at, or null in a repository without commits.</summary>
    /// <param name="known">
    ///     The commit the caller last got from this method, or null. When HEAD still points at it, it is returned without
    ///     starting git, because git could read it then.
    /// </param>
    /// <param name="cancellationToken">Cancels the git commands.</param>
    /// <exception cref="FuseException">git cannot resolve HEAD although the repository has commits, or cannot read the repository at all.</exception>
    public async Task<string?> ResolveAsync(string? known, CancellationToken cancellationToken)
    {
        var head = _gitHead.Read();
        if (head is not null && head == known)
            return head;
        if (head is null)
        {
            var resolved = await ProcessRunner.RunAsync("git", ["rev-parse", "--verify", "-q", "HEAD"], _root.Path, cancellationToken).ConfigureAwait(false);
            if (resolved.ExitCode == 0 && resolved.Output.Trim().Length >= 40)
                head = resolved.Output.Trim();
            else
            {
                var anyCommit = await ProcessRunner.RunAsync("git", ["rev-list", "-n", "1", "--all"], _root.Path, cancellationToken).ConfigureAwait(false);
                if (anyCommit.ExitCode == 0 && anyCommit.Output.Trim().Length == 0)
                    return null; // No commits yet: everything is new.
                throw GitFailure(_root, "resolving HEAD", anyCommit.ExitCode != 0 ? anyCommit : resolved);
            }
        }

        // A new HEAD must be readable, or every file would look new and a broken commit would pass as clean.
        var readable = await ProcessRunner.RunAsync("git", ["cat-file", "-e", head + "^{tree}"], _root.Path, cancellationToken).ConfigureAwait(false);
        if (readable.ExitCode != 0)
            throw GitFailure(_root, $"reading commit {head[..Math.Min(12, head.Length)]}", readable);
        return head;
    }

    /// <summary>
    ///     The failure for a git command that could not read the repository, quoting the first line git printed. Resolving
    ///     HEAD and <see cref="GitStatus"/> report it in the same words.
    /// </summary>
    /// <param name="root">The repository git was asked about.</param>
    /// <param name="action">What git was doing, as the message names it, such as "resolving HEAD" or "git status".</param>
    /// <param name="result">The failed command's exit code and output.</param>
    public static FuseException GitFailure(RepoRoot root, string action, ProcessResult result)
    {
        var detail = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? $"exit code {result.ExitCode}";
        return new FuseException(ErrorCode.LoadFailed, $"git failed {action} in {root.Path} ({detail}); the repository may be damaged, see `git status` and `git fsck`");
    }
}
