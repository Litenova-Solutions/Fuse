using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Fuse.Dotnet;
using Fuse.Failures;
using Fuse.Paths;
using Fuse.Repo;

namespace Fuse.Operations;

/// <summary>The full results of the last build, which the console answer caps.</summary>
internal static class BuildResultsFile
{
    /// <summary>The scope a reuse record holds for a build of the whole solution.</summary>
    internal const string WholeScope = "whole";

    /// <summary>The scope a reuse record holds for a build of the changed projects.</summary>
    internal const string ChangedScope = "changed";

    /// <summary>Past this many changed files the reuse stamp is skipped, because hashing every file costs more than the reuse saves.</summary>
    internal const int MaxReuseFiles = 500;

    /// <summary>Past this many restore outputs the reuse stamp is skipped; no repository restores that many projects.</summary>
    internal const int MaxRestoreOutputs = 1000;

    private const int Version = 1;

    private const string ReuseFileName = "last-build.json";

    /// <summary>
    ///     Every error and warning of a build, as <c>last-build-results.txt</c> in the state directory, for the agent to
    ///     read whole, and with <paramref name="json"/> the errors again as JSON lines in <c>last-build-results.json</c>.
    ///     Always replaces the previous files, even when the build found nothing. Null when the text file could not be written.
    /// </summary>
    /// <param name="treeStamp">The stamp the header carries, or null for none.</param>
    internal static string? WriteBuild(RepoRoot root, IReadOnlyList<BuildDiagnostic> errors, IReadOnlyList<BuildWarning> warnings, bool json, string? treeStamp = null)
    {
        var text = new StringBuilder();
        text.Append($"fuse build: {errors.Count} error(s){treeStamp}").Append('\n');
        foreach (var error in errors)
            text.Append(error).Append('\n');
        if (warnings.Count > 0)
        {
            text.Append("## warnings").Append('\n');
            foreach (var warning in warnings)
                text.Append(warning).Append('\n');
        }

        var path = ResultsFile.Write(root, "last-build-results.txt", text.ToString());
        if (json)
            ResultsFile.Write(root, "last-build-results.json", errors.Count == 0 ? "" : string.Join('\n', errors.Select(e => DiagnosticJson.BuildLine(e))) + "\n");
        return path;
    }

    /// <summary>
    ///     The stamp a build or a test run is reused by: the tree stamp over the changed sources, the stamp over every
    ///     changed file, and the stamp over the restore outputs, with how many sources changed. The tree stamp names the
    ///     tree an answer prints; the other two make a change to a project file, a solution, or anything else the build
    ///     reads miss. Null when git cannot list the changes or the tree is past the bounds above, which answers without
    ///     reuse. It never throws.
    /// </summary>
    internal static async Task<(string TreeHash, string FullHash, string AssetsHash, int SourceCount)?> StampForReuseAsync(RepoRoot root, CancellationToken cancellationToken)
    {
        try
        {
            var changed = await GitStatus.ChangedPathsAsync(root, cancellationToken).ConfigureAwait(false);
            if (changed.Count > MaxReuseFiles)
                return null;
            var sources = changed.Where(path => PathRules.IsSource(path.Absolute)).Select(path => (path.Relative, path.Absolute)).ToList();
            var assets = RestoreOutputs(root.Path);
            if (assets is null)
                return null;
            return (TreeStamp.Of(sources), TreeStamp.Of([.. changed.Select(path => (path.Relative, path.Absolute))]), TreeStamp.Of(assets), sources.Count);
        }
        catch (Exception e) when (e is FuseException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Every restore output under <paramref name="root"/> as the stamp hashes it, or null when they cannot be listed.
    ///     A restore writes beside the projects, outside what git reports, so without this a build after a restore would
    ///     reuse the build from before it.
    /// </summary>
    private static List<(string Relative, string Absolute)>? RestoreOutputs(string root)
    {
        try
        {
            var outputs = new List<(string Relative, string Absolute)>();
            foreach (var file in Directory.EnumerateFiles(root, "project.assets.json", SearchOption.AllDirectories))
            {
                if (!PathRules.IsBuildOutput(root, file))
                    continue;
                outputs.Add((Path.GetRelativePath(root, file).Replace('\\', '/'), file));
                if (outputs.Count > MaxRestoreOutputs)
                    return null;
            }

            return outputs;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Replaces the reuse record with the finished build: what it built and what it found, so an identical build
    ///     prints this instead of building again. The file is replaced atomically, through a temporary file beside it.
    ///     It never throws.
    /// </summary>
    /// <param name="stamp">The reuse stamp of the tree the build ran on.</param>
    /// <param name="scope"><see cref="WholeScope"/> for the whole solution, <see cref="ChangedScope"/> for the changed projects.</param>
    /// <param name="projects">The absolute project files a changed build built, or none for the whole solution.</param>
    /// <param name="arguments">The user's build arguments, unchanged.</param>
    /// <param name="workingDirectory">Where the build ran, as the reuse key holds it.</param>
    /// <param name="finishedUtc">When the build finished.</param>
    /// <param name="runs">One entry per build, in the order it ran.</param>
    /// <param name="treeStamp">The stamp the summary carried, or null for none.</param>
    /// <param name="fileTotal">How many errors the results file holds when it holds more than one build's, or null.</param>
    internal static void WriteReuse(
        RepoRoot root,
        (string TreeHash, string FullHash, string AssetsHash, int SourceCount) stamp,
        string scope,
        IReadOnlyList<string> projects,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        DateTime finishedUtc,
        IReadOnlyList<(string Label, int ExitCode, double Seconds, IReadOnlyList<BuildDiagnostic> Errors, IReadOnlyList<BuildWarning> Warnings, int WarningsCount, IReadOnlyList<(string Command, List<string> Lines)> Commands, string? Times, string Tail, string? RestoreTarget)> runs,
        string? treeStamp,
        int? fileTotal)
    {
        try
        {
            var stored = new JsonArray();
            foreach (var run in runs)
            {
                var node = new JsonObject
                {
                    ["label"] = run.Label,
                    ["exitCode"] = run.ExitCode,
                    ["seconds"] = run.Seconds,
                    ["errors"] = new JsonArray([.. run.Errors.Select(ErrorNode)]),
                    ["warnings"] = new JsonArray([.. run.Warnings.Select(WarningNode)]),
                    ["warningsCount"] = run.WarningsCount,
                    ["commands"] = new JsonArray([.. run.Commands.Select(CommandNode)]),
                    ["tail"] = run.Tail,
                };
                if (run.Times is not null)
                    node["times"] = run.Times;
                if (run.RestoreTarget is not null)
                    node["restoreTarget"] = run.RestoreTarget;
                stored.Add((JsonNode)node);
            }

            var record = new JsonObject
            {
                ["version"] = Version,
                ["treeHash"] = stamp.TreeHash,
                ["fullHash"] = stamp.FullHash,
                ["assetsHash"] = stamp.AssetsHash,
                ["scope"] = scope,
                ["projects"] = new JsonArray([.. projects.Select(project => (JsonNode)JsonValue.Create(project))]),
                ["arguments"] = new JsonArray([.. arguments.Select(argument => (JsonNode)JsonValue.Create(argument))]),
                ["workingDirectory"] = workingDirectory,
                ["finishedUtc"] = finishedUtc.ToString("o", CultureInfo.InvariantCulture),
                ["runs"] = stored,
            };
            if (treeStamp is not null)
                record["treeStamp"] = treeStamp;
            if (fileTotal is not null)
                record["fileTotal"] = fileTotal.Value;

            Directory.CreateDirectory(root.StateDirectory);
            var path = Path.Combine(root.StateDirectory, ReuseFileName);
            var temp = path + ".fuse-tmp";
            File.WriteAllText(temp, record.ToJsonString());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            try
            {
                File.Delete(Path.Combine(root.StateDirectory, ReuseFileName + ".fuse-tmp"));
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    ///     The last finished build's reuse record: its stamp, what it built, when it finished, and one entry per build.
    ///     Null when no build is recorded, or the file is unreadable, malformed, or of another version. It never throws.
    /// </summary>
    internal static (string TreeHash, string FullHash, string AssetsHash, string Scope, List<string> Projects, List<string> Arguments, string WorkingDirectory, DateTime FinishedUtc, List<(string Label, int ExitCode, double Seconds, List<BuildDiagnostic> Errors, List<BuildWarning> Warnings, int WarningsCount, List<(string Command, List<string> Lines)> Commands, string? Times, string Tail, string? RestoreTarget)> Runs, string? TreeStamp, int? FileTotal)? ReadReuse(RepoRoot root)
    {
        try
        {
            var path = Path.Combine(root.StateDirectory, ReuseFileName);
            if (!File.Exists(path))
                return null;
            var parsed = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (parsed is null
                || (int?)parsed["version"] != Version
                || parsed["treeHash"]?.GetValue<string>() is not string treeHash
                || parsed["fullHash"]?.GetValue<string>() is not string fullHash
                || parsed["assetsHash"]?.GetValue<string>() is not string assetsHash
                || parsed["scope"]?.GetValue<string>() is not string scope
                || StringList(parsed["projects"]) is not { } projects
                || StringList(parsed["arguments"]) is not { } buildArguments
                || parsed["workingDirectory"]?.GetValue<string>() is not string workingDirectory
                || FinishedAt(parsed["finishedUtc"]) is not { } finishedUtc
                || parsed["runs"] is not JsonArray runs)
                return null;
            var stored = new List<(string Label, int ExitCode, double Seconds, List<BuildDiagnostic> Errors, List<BuildWarning> Warnings, int WarningsCount, List<(string Command, List<string> Lines)> Commands, string? Times, string Tail, string? RestoreTarget)>(runs.Count);
            foreach (var run in runs)
            {
                if (run is not JsonObject entry
                    || entry["label"]?.GetValue<string>() is not string label
                    || entry["exitCode"]?.GetValue<int>() is not int exitCode
                    || Number(entry["seconds"]) is not { } seconds
                    || ErrorList(entry["errors"]) is not { } errors
                    || WarningList(entry["warnings"]) is not { } warnings
                    || entry["warningsCount"]?.GetValue<int>() is not int warningsCount
                    || CommandList(entry["commands"]) is not { } commands
                    || entry["tail"]?.GetValue<string>() is not string tail)
                    return null;
                stored.Add((label, exitCode, seconds, errors, warnings, warningsCount, commands, entry["times"]?.GetValue<string>(), tail, entry["restoreTarget"]?.GetValue<string>()));
            }

            return (treeHash, fullHash, assetsHash, scope, projects, buildArguments, workingDirectory, finishedUtc, stored, parsed["treeStamp"]?.GetValue<string>(), parsed["fileTotal"] is JsonValue total ? total.GetValue<int>() : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>How long ago <paramref name="finishedUtc"/> was, as a reuse line says it: whole seconds, minutes, hours, or days. A time in the future reads as no time at all.</summary>
    internal static string AgeAgo(DateTime finishedUtc, DateTime utcNow)
    {
        var age = utcNow - finishedUtc;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        if (age.TotalSeconds < 90)
            return $"{(int)age.TotalSeconds} s";
        if (age.TotalMinutes < 90)
            return $"{(int)age.TotalMinutes} min";
        if (age.TotalHours < 48)
            return $"{(int)age.TotalHours} h";
        return $"{(int)age.TotalDays} d";
    }

    /// <summary><paramref name="error"/> as JSON: a general error leaves its line and column out, as the text answer does.</summary>
    private static JsonObject ErrorNode(BuildDiagnostic error)
    {
        var node = new JsonObject
        {
            ["file"] = error.File,
            ["code"] = error.Code,
            ["message"] = error.Message,
        };
        if (error.Line is not null)
            node["line"] = error.Line.Value;
        if (error.Column is not null)
            node["column"] = error.Column.Value;
        return node;
    }

    /// <summary><paramref name="warning"/> as JSON.</summary>
    private static JsonObject WarningNode(BuildWarning warning) => new()
    {
        ["file"] = warning.File,
        ["line"] = warning.Line,
        ["column"] = warning.Column,
        ["code"] = warning.Code,
        ["message"] = warning.Message,
    };

    /// <summary><paramref name="command"/> as JSON: the failed command and the last lines it printed.</summary>
    private static JsonObject CommandNode((string Command, List<string> Lines) command) => new()
    {
        ["command"] = command.Command,
        ["lines"] = new JsonArray([.. command.Lines.Select(line => (JsonNode)JsonValue.Create(line))]),
    };

    /// <summary><paramref name="node"/> as strings, or null when it is no array of them.</summary>
    private static List<string>? StringList(JsonNode? node)
    {
        if (node is not JsonArray array)
            return null;
        var result = new List<string>(array.Count);
        foreach (var entry in array)
        {
            if (entry?.GetValue<string>() is not string value)
                return null;
            result.Add(value);
        }

        return result;
    }

    /// <summary><paramref name="node"/> as build errors, or null when it is no array of them. An entry without a line or a column reads as a general error.</summary>
    private static List<BuildDiagnostic>? ErrorList(JsonNode? node)
    {
        if (node is not JsonArray array)
            return null;
        var result = new List<BuildDiagnostic>(array.Count);
        foreach (var entry in array)
        {
            if (entry is not JsonObject stored
                || stored["file"]?.GetValue<string>() is not string file
                || stored["code"]?.GetValue<string>() is not string code
                || stored["message"]?.GetValue<string>() is not string message)
                return null;
            result.Add(new BuildDiagnostic(file, stored["line"] is JsonValue line ? line.GetValue<int>() : null, stored["column"] is JsonValue column ? column.GetValue<int>() : null, code, message));
        }

        return result;
    }

    /// <summary><paramref name="node"/> as build warnings, or null when it is no array of them.</summary>
    private static List<BuildWarning>? WarningList(JsonNode? node)
    {
        if (node is not JsonArray array)
            return null;
        var result = new List<BuildWarning>(array.Count);
        foreach (var entry in array)
        {
            if (entry is not JsonObject stored
                || stored["file"]?.GetValue<string>() is not string file
                || stored["line"]?.GetValue<int>() is not int line
                || stored["column"]?.GetValue<int>() is not int column
                || stored["code"]?.GetValue<string>() is not string code
                || stored["message"]?.GetValue<string>() is not string message)
                return null;
            result.Add(new BuildWarning(file, line, column, code, message));
        }

        return result;
    }

    /// <summary><paramref name="node"/> as failed commands, or null when it is no array of them.</summary>
    private static List<(string Command, List<string> Lines)>? CommandList(JsonNode? node)
    {
        if (node is not JsonArray array)
            return null;
        var result = new List<(string Command, List<string> Lines)>(array.Count);
        foreach (var entry in array)
        {
            if (entry is not JsonObject stored
                || stored["command"]?.GetValue<string>() is not string command
                || StringList(stored["lines"]) is not { } lines)
                return null;
            result.Add((command, lines));
        }

        return result;
    }

    /// <summary><paramref name="node"/> as a number of seconds, or null when it holds none.</summary>
    private static double? Number(JsonNode? node) =>
        node?.ToString() is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;

    /// <summary><paramref name="node"/> as the moment the build finished, or null when it names none.</summary>
    private static DateTime? FinishedAt(JsonNode? node) =>
        node?.GetValue<string>() is string text && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var finished) ? finished : null;
}
