using System.Text;
using Fuse.Changes;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Testing.Caching;
using Fuse.Testing.Model;
using Fuse.Testing.Selection.ProjectFiles;
using Fuse.Workspace.Generations;
using Fuse.Workspace.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
namespace Fuse.Testing.Selection;

/// <summary>
///     Selects the tests that can observe the working-tree changes. It takes the <see cref="TypeWalk"/>'s class-level
///     answer, and refines it to test methods with the <see cref="MemberWalk"/> when few classes are reachable and the
///     walk finishes within its budget.
/// </summary>
/// <remarks>
///     A missed failing test is the one outcome selection must not produce; running extra tests only costs time. So when
///     the refinement would cost more than it saves, or does not finish, the class-level answer stands. A selection
///     reads only the generation it is given: it asks the writer to load the reached projects, pins the generation
///     that load publishes, and walks that generation's views, so concurrent selections and checks read apart.
/// </remarks>
internal sealed class TestSelector
{
    // Above this many reachable test classes the refinement rarely finishes within the walk's own 300-symbol and
    // 8-second budget, and only a walk that finishes has visited every caller, which is what makes its answer safe.
    private const int RefineThreshold = 100;

    private static readonly CSharpParseOptions DefaultSyntaxParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    private readonly RepoWorkspace _workspace;
    private readonly TimeProvider _time;
    private readonly DynamicTestMap _dynamicMap;
    private readonly TypeWalk _types;
    private readonly SyntaxTokenIndex _tokenIndex;
    private volatile bool _tokenIndexLoaded;

    /// <param name="workspace">The engine's workspace, which the selector loads the changed projects and their dependents into.</param>
    /// <param name="time">Measures the member walk against its budget.</param>
    public TestSelector(RepoWorkspace workspace, TimeProvider time)
        : this(workspace, time, new DynamicTestMap(workspace.Root.SharedStateDirectory))
    {
    }

    internal TestSelector(RepoWorkspace workspace, TimeProvider time, DynamicTestMap dynamicMap)
    {
        _workspace = workspace;
        _time = time;
        _dynamicMap = dynamicMap;
        _types = new TypeWalk(workspace, dynamicMap);
        _tokenIndex = new SyntaxTokenIndex(workspace.Root, workspace.Log);
    }

    /// <summary>Dynamic coverage map whose recorded tests are added to the selection, never replacing a static selection.</summary>
    internal DynamicTestMap DynamicMap => _dynamicMap;

    /// <summary>Syntax token and inheritance index used to narrow symbol searches.</summary>
    internal SyntaxTokenIndex TokenIndex => _tokenIndex;

    /// <summary>Selects tests for <paramref name="changedFiles"/> from the workspace's pinned generation; the result holds each test project file's selection.</summary>
    /// <param name="changedFiles">Changed files, C# sources and non-C# files alike.</param>
    /// <param name="cancellationToken">Cancels loading and both walks.</param>
    public async Task<SelectionResult> SelectAsync(IReadOnlyList<RepoPath> changedFiles, CancellationToken cancellationToken)
    {
        var (result, _) = await SelectAsync(changedFiles, _workspace.PinGeneration(), cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    ///     Selects tests for <paramref name="changedFiles"/> from <paramref name="generation"/>, loading the reached
    ///     projects through the writer first. The generation the load publishes travels with the result, because
    ///     it alone holds the loaded projects the selections were made from.
    /// </summary>
    /// <param name="changedFiles">Changed files, C# sources and non-C# files alike.</param>
    /// <param name="generation">The generation the plan pinned, whose graph and views the selection reads.</param>
    /// <param name="cancellationToken">Cancels loading and both walks.</param>
    public async Task<(SelectionResult Result, Generation Generation)> SelectAsync(IReadOnlyList<RepoPath> changedFiles, Generation generation, CancellationToken cancellationToken)
    {
        var graph = generation.Graph;
        var projectFileWholes = ProjectFileSelection.Select(graph, _workspace.Tracker.ReadHead, changedFiles);
        var nonSourceOwners = new Dictionary<RepoPath, IReadOnlyList<ProjectNode>>();
        foreach (var path in changedFiles.Where(p => !PathRules.IsSource(p.Absolute)))
        {
            // A project file defines how its project evaluates, and the sync has already evaluated it again; it is
            // not a file the project holds for its tests to read, so the walks select no project whole for it.
            // ProjectFileSelection selects whole for the named project files above instead. That keeps a
            // project's own file, such as the one a fixture rewrites to name a container package, from widening
            // the selection its code changes give.
            if (PathRules.IsProjectFile(path.Absolute))
                continue;
            var owners = graph.NonSourceOwnersOf(path);
            if (owners.Count == 0 && graph.ContainingProject(path) is { } containing)
                owners = [containing];
            if (owners.Count > 0)
                nonSourceOwners[path] = owners;
        }

        var changedProjects = changedFiles.SelectMany(p => PathRules.IsSource(p.Absolute) ? graph.OwnersOf(p) : nonSourceOwners.GetValueOrDefault(p, [])).DistinctBy(p => p.Path).ToList();
        var reachedProjects = changedProjects.Concat(changedProjects.SelectMany(graph.DependentsOf)).DistinctBy(p => p.Path).ToList();
        if (!reachedProjects.Any(p => p.IsTest))
            return (new SelectionResult(WholeSelections(projectFileWholes), 0, false), generation);

        // Syntax-first short-circuit: run CodeDiff.Find on syntax BEFORE loading dependent projects or binding SemanticModels.
        var (activeFiles, allCSharp, hasTopLevelDiff) = await FilterChangedFilesBySyntaxAsync(changedFiles, generation, cancellationToken).ConfigureAwait(false);
        if (activeFiles.Count == 0)
        {
            var syntaxSameCode = changedFiles.Count > 0
                                 && changedFiles.All(p => PathRules.IsSource(p.Absolute) && graph.OwnersOf(p).Count > 0);
            _workspace.Log("test selection: syntax diff empty across all changed C# files; short-circuiting without loading dependents");
            return (new SelectionResult(new Dictionary<RepoPath, TestSelection>(), 0, syntaxSameCode), generation);
        }

        var activeChangedProjects = activeFiles.SelectMany(p => PathRules.IsSource(p.Absolute) ? graph.OwnersOf(p) : nonSourceOwners.GetValueOrDefault(p, [])).DistinctBy(p => p.Path).ToList();
        var effectiveReachedProjects = activeChangedProjects.Concat(activeChangedProjects.SelectMany(graph.DependentsOf)).DistinctBy(p => p.Path).ToList();
        if (!effectiveReachedProjects.Any(p => p.IsTest))
            return (new SelectionResult(WholeSelections(projectFileWholes), 0, false), generation);

        // If all active changes are pure C# member/body edits and there are unloaded dependents, load only the owner
        // projects first so local-only edits (renames, var rewrites) short-circuit without loading dependent projects.
        if (allCSharp && !hasTopLevelDiff && effectiveReachedProjects.Count > activeChangedProjects.Count)
        {
            generation = await _workspace.EnsureLoadedAndPinAsync(activeChangedProjects, cancellationToken).ConfigureAwait(false);
            graph = generation.Graph;
            var ownerSolution = generation.Current;
            var ownerRoslynProjects = activeChangedProjects.SelectMany(n => RepoWorkspace.ProjectsFor(ownerSolution, n)).ToList();
            await PrepareTokenIndexAsync(ownerSolution, ownerRoslynProjects, cancellationToken).ConfigureAwait(false);
            var ownerWalk = new MemberWalk(_workspace.Root, generation, ownerSolution, ownerRoslynProjects, _time, _dynamicMap, _tokenIndex);
            foreach (var path in activeFiles)
                await ownerWalk.SeedAsync(path, cancellationToken).ConfigureAwait(false);

            var ownerAllSameCode = ownerWalk.DroppedSameCode == ownerWalk.ChangedDeclarations
                                   && ownerWalk.Selections.Count == 0
                                   && changedFiles.Count > 0
                                   && changedFiles.All(p => PathRules.IsSource(p.Absolute))
                                   && (ownerWalk.ChangedDeclarations > 0 || activeFiles.All(p => ownerSolution.GetDocumentIdsWithFilePath(p.Absolute).Length > 0));
            if (ownerAllSameCode)
            {
                if (ownerWalk.DroppedSameCode > 0)
                    _workspace.Log($"test selection: {ownerWalk.DroppedSameCode} of {ownerWalk.ChangedDeclarations} changed declaration(s) compile to the same code as HEAD; selecting no tests for them");
                return (new SelectionResult(new Dictionary<RepoPath, TestSelection>(), ownerWalk.DroppedSameCode, true), generation);
            }
        }

        generation = await _workspace.EnsureLoadedAndPinAsync(effectiveReachedProjects, cancellationToken).ConfigureAwait(false);
        graph = generation.Graph;

        var solution = generation.Current;
        var reachedRoslynProjects = effectiveReachedProjects.SelectMany(n => RepoWorkspace.ProjectsFor(solution, n)).ToList();
        await PrepareTokenIndexAsync(solution, reachedRoslynProjects, cancellationToken).ConfigureAwait(false);
        var walk = new MemberWalk(_workspace.Root, generation, solution, reachedRoslynProjects, _time, _dynamicMap, _tokenIndex);
        var seeds = new List<(Document Document, SyntaxNode Node)>();
        foreach (var path in activeFiles)
        {
            if (!PathRules.IsSource(path.Absolute))
            {
                // A non-C# file has no declaration either walk can start from. A file an MSBuild item names, and a
                // file under a project directory that no item names, belongs to its project, so every test project
                // that depends on that project runs whole. A file outside every project selects nothing here; the
                // plan names it.
                if (nonSourceOwners.TryGetValue(path, out var nonSourceOwnerList))
                {
                    foreach (var owner in nonSourceOwnerList)
                        walk.SelectDependentsWhole(owner, $"{path.FileName} changed");
                }

                continue;
            }

            if (!path.Absolute.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                // A Razor file (.razor, .cshtml) has no declaration either walk can start from, so every test project
                // that depends on its project runs whole.
                foreach (var owner in graph.OwnersOf(path))
                    walk.SelectDependentsWhole(owner, $"{path.FileName} changed");
                continue;
            }

            seeds.AddRange(await walk.SeedAsync(path, cancellationToken).ConfigureAwait(false));
        }

        if (walk.DroppedSameCode > 0)
            _workspace.Log($"test selection: {walk.DroppedSameCode} of {walk.ChangedDeclarations} changed declaration(s) compile to the same code as HEAD; selecting no tests for them");

        // The projects the seeds selected whole start the class-level answer, and a whole selection from the type walk
        // replaces theirs. The type walk's methods add nothing to a whole project.
        var seededWhole = walk.Selections.Where(s => s.Value is TestSelection.Whole).ToList();
        var classLevel = new Dictionary<RepoPath, TestSelection>(seededWhole);
        foreach (var (path, selection) in await _types.SelectAsync(reachedRoslynProjects, seeds, graph, _workspace.Root, cancellationToken).ConfigureAwait(false))
        {
            if (selection is TestSelection.Whole || !classLevel.ContainsKey(path))
                classLevel[path] = selection;
        }

        // The class-level answer costs milliseconds. The member walk costs a reference search per symbol, so it runs only
        // when the class-level answer is small enough for the refinement to matter and the walk to finish.
        var classes = classLevel.Values.OfType<TestSelection.Methods>().Sum(m => m.Patterns.Count);
        // Every changed declaration the walks were seeded with compiled to the same code, or none was: a comment or
        // whitespace edit differs from HEAD in no declaration the code differs by, which is the same answer. A changed
        // non-C# file is not that answer, so it keeps the summary the plan gives it instead.
        var allSameCode = walk.DroppedSameCode == walk.ChangedDeclarations
                          && changedFiles.Count > 0
                          && changedFiles.All(p => PathRules.IsSource(p.Absolute))
                          && (walk.ChangedDeclarations > 0 || changedFiles.All(p => solution.GetDocumentIdsWithFilePath(p.Absolute).Length > 0));
        if (classes > RefineThreshold)
        {
            _workspace.Log($"test selection: {classes} test classes reachable; selecting at class level");
            AddBuildOnlySelections(graph, activeChangedProjects, effectiveReachedProjects, classLevel);
            AddProjectFileWholes(projectFileWholes, classLevel);
            return (new SelectionResult(classLevel, walk.DroppedSameCode, allSameCode), generation);
        }

        var outcome = await walk.RunAsync(cancellationToken).ConfigureAwait(false);
        if (!outcome.Completed)
        {
            _workspace.Log($"test selection: member walk over budget in {outcome.Milliseconds} ms, {outcome.Symbols} symbol(s), {classes} test class(es) reachable, {PatternsOf(classLevel)} pattern(s) selected; selecting at class level");
            AddBuildOnlySelections(graph, activeChangedProjects, effectiveReachedProjects, classLevel);
            AddProjectFileWholes(projectFileWholes, classLevel);
            return (new SelectionResult(classLevel, walk.DroppedSameCode, allSameCode), generation);
        }

        // A project the seeds selected whole reports the class-level answer's reason, so the summary gives the same
        // reason for it whichever answer is returned.
        var memberLevel = new Dictionary<RepoPath, TestSelection>(walk.Selections);
        foreach (var (path, _) in seededWhole)
            memberLevel[path] = classLevel[path];
        AddBuildOnlySelections(graph, activeChangedProjects, effectiveReachedProjects, memberLevel);
        AddProjectFileWholes(projectFileWholes, memberLevel);
        _workspace.Log($"test selection: member walk completed in {outcome.Milliseconds} ms, {outcome.Symbols} symbol(s), {classes} test class(es) reachable, {PatternsOf(memberLevel)} pattern(s) selected; selecting at member level");
        return (new SelectionResult(memberLevel, walk.DroppedSameCode, allSameCode), generation);
    }

    /// <summary>
    ///     Loads the syntax token index once and brings it current over the searched projects, so the member walk
    ///     narrows its reference searches to candidate documents. The walk searches as before when the index has
    ///     nothing to narrow by.
    /// </summary>
    private async Task PrepareTokenIndexAsync(Solution solution, IReadOnlyList<Project> reachedProjects, CancellationToken cancellationToken)
    {
        if (!_tokenIndexLoaded)
        {
            _tokenIndex.TryLoad("current");
            _tokenIndexLoaded = true;
        }

        await _tokenIndex.EnsureIndexedAsync(solution, reachedProjects, cancellationToken).ConfigureAwait(false);
        _tokenIndex.SaveIfDirty("current");
    }

    private async Task<(List<RepoPath> ActiveFiles, bool AllCSharp, bool HasTopLevelDiff)> FilterChangedFilesBySyntaxAsync(
        IReadOnlyList<RepoPath> changedFiles,
        Generation generation,
        CancellationToken cancellationToken)
    {
        var active = new List<RepoPath>();
        var allCSharp = true;
        var hasTopLevelDiff = false;

        foreach (var path in changedFiles)
        {
            if (!PathRules.IsSource(path.Absolute) || !path.Absolute.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                active.Add(path);
                allCSharp = false;
                continue;
            }

            if (!File.Exists(path.Absolute))
            {
                active.Add(path);
                continue;
            }

            var docIds = generation.Current.GetDocumentIdsWithFilePath(path.Absolute);
            if (docIds.Length > 0)
            {
                var doc = generation.Current.GetDocument(docIds[0]);
                var baseDoc = generation.Baseline.GetDocument(docIds[0]);
                if (doc is not null)
                {
                    // One document parses with one target framework's symbols, so an edit inside an #if branch another
                    // framework compiles looks like no change here. A file with directives goes to the semantic walk,
                    // which sees every framework's project, as the disk comparison below does.
                    var currentText = await doc.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    var headText = baseDoc is null ? null : await baseDoc.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    if (ContainsPreprocessorDirective(currentText) || (headText is not null && ContainsPreprocessorDirective(headText)))
                    {
                        active.Add(path);
                        continue;
                    }

                    var afterRoot = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                    if (afterRoot is not null)
                    {
                        var beforeRoot = headText is null
                            ? null
                            : await CSharpSyntaxTree.ParseText(headText, (CSharpParseOptions)afterRoot.SyntaxTree.Options, cancellationToken: cancellationToken).GetRootAsync(cancellationToken).ConfigureAwait(false);
                        var loadedDiffs = CodeDiff.Find(beforeRoot, afterRoot);
                        if (loadedDiffs.Count > 0)
                        {
                            active.Add(path);
                            hasTopLevelDiff |= loadedDiffs.Any(d => d is CompilationUnitSyntax);
                        }

                        continue;
                    }
                }
            }

            byte[] diskBytes;
            try
            {
                diskBytes = await SharedRead.AllBytesAsync(path.Absolute, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                active.Add(path);
                continue;
            }

            var headBytes = _workspace.Tracker.ReadHead(path);
            var diskSource = DecodeSourceText(diskBytes);
            var headSource = headBytes is null ? null : DecodeSourceText(headBytes);
            if (ContainsPreprocessorDirective(diskSource) || (headSource is not null && ContainsPreprocessorDirective(headSource)))
            {
                active.Add(path);
                continue;
            }

            var afterSyntax = await CSharpSyntaxTree.ParseText(diskSource, DefaultSyntaxParseOptions, path.Absolute, cancellationToken).GetRootAsync(cancellationToken).ConfigureAwait(false);
            var beforeSyntax = headSource is null
                ? null
                : await CSharpSyntaxTree.ParseText(headSource, DefaultSyntaxParseOptions, path.Absolute, cancellationToken).GetRootAsync(cancellationToken).ConfigureAwait(false);
            var diffs = CodeDiff.Find(beforeSyntax, afterSyntax);
            if (diffs.Count > 0)
            {
                active.Add(path);
                hasTopLevelDiff |= diffs.Any(d => d is CompilationUnitSyntax);
            }
        }

        return (active, allCSharp, hasTopLevelDiff);
    }

    private static SourceText DecodeSourceText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return SourceText.From(stream, Encoding.UTF8);
    }

    private static bool ContainsPreprocessorDirective(SourceText text)
    {
        foreach (var line in text.Lines)
        {
            var span = line.ToString().AsSpan().TrimStart();
            if (span.StartsWith("#if", StringComparison.Ordinal) || span.StartsWith("#elif", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Selects every test project that reaches an application through a reference with ReferenceOutputAssembly
    ///     false whole. Such a test starts the application as a child process, so neither walk follows it to a test by
    ///     name, and a change in the application, or in code an application that was reached uses, selects it through
    ///     the application host rule.
    /// </summary>
    private static void AddBuildOnlySelections(
        RepoGraph graph,
        IReadOnlyList<ProjectNode> changedProjects,
        IReadOnlyList<ProjectNode> reachedProjects,
        Dictionary<RepoPath, TestSelection> selections)
    {
        // Recorded coverage the walks already added stays; these whole-project selections stand beside it, so a
        // test the static walks selected is never dropped.
        foreach (var application in reachedProjects.Where(p => p.IsExecutable))
        {
            var reason = changedProjects.Any(p => p.Path == application.Path)
                ? $"{application.Name} changed and runs behind an application host"
                : $"{application.Name} uses the changed code and runs behind an application host";
            foreach (var test in HostRule.BuildOnlyDependentTestProjects(graph, application))
            {
                if (selections.GetValueOrDefault(test.Path) is not TestSelection.Whole)
                    selections[test.Path] = new TestSelection.Whole(reason);
            }
        }
    }

    /// <summary>How many test name patterns the selections hold.</summary>
    private static int PatternsOf(IReadOnlyDictionary<RepoPath, TestSelection> selections) =>
        selections.Values.OfType<TestSelection.Methods>().Sum(m => m.Patterns.Count);

    /// <summary>The whole selections project-file changes call for, as test selections.</summary>
    private static Dictionary<RepoPath, TestSelection> WholeSelections(IReadOnlyDictionary<RepoPath, string> wholes)
    {
        var selections = new Dictionary<RepoPath, TestSelection>();
        foreach (var (path, reason) in wholes)
            selections[path] = new TestSelection.Whole(reason);
        return selections;
    }

    /// <summary>
    ///     Adds the whole selections project-file changes call for. A project the walks selected by name runs whole
    ///     instead, and a project already selected whole keeps the first reason.
    /// </summary>
    private static void AddProjectFileWholes(IReadOnlyDictionary<RepoPath, string> wholes, Dictionary<RepoPath, TestSelection> selections)
    {
        foreach (var (path, reason) in wholes)
        {
            if (selections.GetValueOrDefault(path) is not TestSelection.Whole)
                selections[path] = new TestSelection.Whole(reason);
        }
    }
}
