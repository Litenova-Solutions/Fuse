namespace Fuse.Evals;

/// <summary>
///     Draws the cases of the correctness and selection suites. A plan depends only on the repository's tree, the seed and
///     the requested count, never on Fuse, so the jobs that run the truth side and the job that measures Fuse draw the
///     same cases.
/// </summary>
internal static class CasePlanner
{
    /// <summary>
    ///     The correctness cases: each changes one declaration, or two or three in different projects, with
    ///     <see cref="CompileMutator"/>. Every edit is drawn from the clean tree, as each case starts from HEAD.
    /// </summary>
    public static List<PlannedCase> Correctness(EvalRepo repo, SolutionInfo solution, int count, int seed)
    {
        var sources = solution.CodeSources();
        var random = new Random(seed);
        var cases = new List<PlannedCase>();
        for (var i = 0; i < count; i++)
        {
            var editCount = random.NextDouble() < 0.7 ? 1 : random.Next(2, 4);
            var edits = PickEdits(repo, solution, sources, editCount, random);
            if (edits.Count > 0)
                cases.Add(new PlannedCase(i, edits));
        }

        return cases;
    }

    /// <summary>
    ///     The selection cases: behavior edits to non-test code with <see cref="BehaviorMutator"/>, each kept only when the
    ///     solution still builds with it, because a test run of code that does not compile measures nothing. The draws do
    ///     not depend on which edits build, so the first <paramref name="count"/> that build are the same on every machine.
    /// </summary>
    public static async Task<(List<PlannedCase> Cases, List<string> Skipped)> SelectionAsync(EvalRepo repo, SolutionInfo solution, int count, int seed)
    {
        var sources = solution.CodeSources();
        var random = new Random(seed);
        var cases = new List<PlannedCase>();
        var skipped = new List<string>();
        for (var attempt = 0; cases.Count < count && attempt < count * 8; attempt++)
        {
            var file = sources[random.Next(sources.Count)];
            var kind = BehaviorMutator.Kinds[random.Next(BehaviorMutator.Kinds.Length)];
            var edit = BehaviorMutator.Mutate(file, Path.GetRelativePath(repo.Root, file).Replace('\\', '/'), kind, random);
            if (edit is null)
                continue;
            await File.WriteAllTextAsync(file, edit.NewText);
            var build = await repo.BuildAsync();
            await repo.ResetAsync();
            if (build.ExitCode != 0)
            {
                skipped.Add($"{edit.Kind} {edit.Path}: {edit.Description} (does not compile)");
                continue;
            }

            cases.Add(new PlannedCase(cases.Count, [edit]));
            Console.WriteLine($"[plan] selection case {cases.Count}/{count}: {edit.Kind} {edit.Path}");
        }

        return (cases, skipped);
    }

    /// <summary>Writes the edits of <paramref name="planned"/> into the tree; an edit without new text deletes its file.</summary>
    public static async Task ApplyAsync(EvalRepo repo, PlannedCase planned)
    {
        foreach (var edit in planned.Edits)
        {
            var full = Path.Combine(repo.Root, edit.Path);
            if (edit.NewText is null)
                File.Delete(full);
            else
                await File.WriteAllTextAsync(full, edit.NewText);
        }
    }

    private static List<FileEdit> PickEdits(EvalRepo repo, SolutionInfo solution, List<string> sources, int editCount, Random random)
    {
        var edits = new List<FileEdit>();
        var usedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var attempt = 0; attempt < 200 && edits.Count < editCount; attempt++)
        {
            var file = sources[random.Next(sources.Count)];
            var project = solution.ProjectOf(file) ?? "";
            // Sequences spread over different projects when the solution has more than one.
            if (usedProjects.Contains(project) && solution.CodeProjects.Count > 1)
                continue;
            var kind = CompileMutator.Kinds[random.Next(CompileMutator.Kinds.Length)];
            var edit = CompileMutator.Mutate(file, Path.GetRelativePath(repo.Root, file).Replace('\\', '/'), kind, random);
            if (edit is null)
                continue;
            edits.Add(edit);
            usedProjects.Add(project);
        }

        return edits;
    }
}
