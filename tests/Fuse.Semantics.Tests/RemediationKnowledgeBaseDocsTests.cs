using System.Runtime.CompilerServices;
using Fuse.Semantics.Remediation;
using Xunit;

namespace Fuse.Semantics.Tests;

// C1 docs drift guard: the environment-remediation troubleshooting page is generated from the knowledge base,
// so a test diffs the KB signature ids against the page. If a signature is added to (or removed from) the KB
// without updating the page, this fails, keeping the docs and the shipped KB in sync.
public sealed class RemediationKnowledgeBaseDocsTests
{
    private static string? RepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fuse.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    [Fact]
    public void Every_knowledge_base_signature_id_appears_on_the_troubleshooting_page()
    {
        var root = RepoRoot();
        Assert.NotNull(root);
        var pagePath = Path.Combine(root!, "site", "content", "docs", "reference", "environment-remediation.mdx");
        Assert.True(File.Exists(pagePath), $"troubleshooting page not found at {pagePath}");

        var page = File.ReadAllText(pagePath);
        var kb = RemediationKnowledgeBase.LoadDefault();
        foreach (var signature in kb.Signatures)
            Assert.True(page.Contains(signature.Id, StringComparison.Ordinal),
                $"KB signature '{signature.Id}' is missing from the troubleshooting page (docs/KB drift).");
    }
}
