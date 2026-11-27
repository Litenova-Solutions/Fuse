using System.Text;
using System.Text.Json;
using Fuse.Client;
using Fuse.Operations.Check;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
using Fuse.Protocol.Transport;
namespace Fuse.Operations.Inspection;

/// <summary>
///     <c>fuse graph [--json]</c> and <c>fuse impact &lt;paths...&gt; [--json]</c>: queries the engine for the project
///     DAG, coupling clusters, test ownership, and change impact radius so orchestrators can slice disjoint work
///     before spawning workers.
/// </summary>
internal static class GraphOperation
{
    internal const string GraphUsage = "usage: fuse graph [--json]";
    internal const string ImpactUsage = "usage: fuse impact <paths...> [--json]";

    public static async Task<OperationResult> RunGraphAsync(RepoRoot root, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var json = false;
        foreach (var token in args)
        {
            if (token == "--json")
            {
                json = true;
                continue;
            }

            return new OperationResult(Outcome.Unanswered, $"fuse: graph takes [--json]; '{token}' is not one\n{GraphUsage}");
        }

        var response = await EngineClient.SendAsync(root, new EngineRequest.Graph(), TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        if (response is not EngineResponse.GraphAnswered answered)
            return OperationResult.Unanswered(response, "project graph");

        return RenderGraph(answered.Report, json);
    }

    public static async Task<OperationResult> RunImpactAsync(RepoRoot root, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var json = false;
        var paths = new List<string>();
        var options = true;
        foreach (var token in args)
        {
            if (token == "--" && options)
            {
                options = false;
                continue;
            }

            if (options && token == "--json")
            {
                json = true;
                continue;
            }

            if (options && token.StartsWith("--", StringComparison.Ordinal))
                return new OperationResult(Outcome.Unanswered, $"fuse: impact takes <paths...> [--json]; '{token}' is not one\n{ImpactUsage}");

            paths.Add(token);
        }

        if (paths.Count == 0)
            return new OperationResult(Outcome.Unanswered, ImpactUsage);

        var (resolved, refusal) = CheckOperation.ResolveFiles(paths, Path.GetFullPath);
        if (refusal is not null)
            return refusal;

        var response = await EngineClient.SendAsync(root, new EngineRequest.Impact([.. resolved]), TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        if (response is not EngineResponse.ImpactAnswered answered)
            return OperationResult.Unanswered(response, "impact report");

        return RenderImpact(answered.Report, json);
    }

    internal static OperationResult RenderGraph(GraphReport report, bool json)
    {
        if (json)
            return new OperationResult(Outcome.Clean, JsonSerializer.Serialize(report, ProtocolJson.Default.GraphReport));

        var sb = new StringBuilder();
        sb.Append($"fuse: {report.Projects.Length} project(s), {report.Clusters.Length} cluster(s), {report.TestOwners.Length} test project(s) (tree {report.Tree})");
        foreach (var project in report.Projects)
        {
            var kind = project.IsTestProject ? "test" : "src";
            var refs = project.References.Length > 0 ? $" -> {string.Join(", ", project.References)}" : "";
            sb.Append($"\n  [{kind}] {project.Name} ({project.Path}){refs}");
        }

        foreach (var cluster in report.Clusters)
            sb.Append($"\n  cluster {cluster.Name}: {string.Join(", ", cluster.Projects)}");

        foreach (var owner in report.TestOwners)
            sb.Append($"\n  tests {owner.TestProject}: {string.Join(", ", owner.CoveredProjects)}");

        return new OperationResult(Outcome.Clean, sb.ToString());
    }

    internal static OperationResult RenderImpact(ImpactReport report, bool json)
    {
        if (json)
            return new OperationResult(Outcome.Clean, JsonSerializer.Serialize(report, ProtocolJson.Default.ImpactReport));

        var sb = new StringBuilder();
        sb.Append($"fuse: {(report.Summary.Length > 0 ? report.Summary : $"{report.AffectedProjects.Length} affected project(s), {report.AffectedTestProjects.Length} test project(s)")}");
        if (report.OwningProjects.Length > 0)
            sb.Append($"\n  owning: {string.Join(", ", report.OwningProjects)}");
        if (report.AffectedProjects.Length > 0)
            sb.Append($"\n  affected projects: {string.Join(", ", report.AffectedProjects)}");
        if (report.AffectedTestProjects.Length > 0)
            sb.Append($"\n  affected tests: {string.Join(", ", report.AffectedTestProjects)}");
        if (report.AffectedFiles.Length > 0)
            sb.Append($"\n  affected files: {report.AffectedFiles.Length}");
        if (report.BroadReach)
            sb.Append("\n  broad reach: true");

        return new OperationResult(Outcome.Clean, sb.ToString());
    }
}
