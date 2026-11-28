using System.Text;
using System.Text.Json;
using Fuse.Client;
using Fuse.Operations.Check;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
using Fuse.Protocol.Transport;
namespace Fuse.Operations.Inspection;

/// <summary><c>fuse impact &lt;paths...&gt; [--json]</c>: the change impact radius for the named paths.</summary>
internal static class ImpactOperation
{
    internal const string ImpactUsage = "usage: fuse impact <paths...> [--json]";

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
