using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace Fuse.Cli.Tests.Commands;

// G4: the Fuse.Capture build-target channel. The shipped .targets must be well-formed XML (a stray "--" in a
// comment silently breaks MSBuild import), and it must carry the emit target plus the opt-out property. This
// parses the file the package ships, guarding the import contract without needing a full build.
public sealed class FuseCaptureTargetsTests
{
    private static string? RepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fuse.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    [Fact]
    public void The_targets_file_is_well_formed_and_carries_the_channel_contract()
    {
        var root = RepoRoot();
        Assert.NotNull(root);
        var targetsPath = Path.Combine(root!, "src", "Packaging", "Fuse.Capture", "build", "Fuse.Capture.targets");
        Assert.True(File.Exists(targetsPath), $"the shipped targets file should exist at {targetsPath}");

        // Parses only if the XML is well-formed - this fails on a "--" inside a comment, the trap that silently
        // breaks the MSBuild import.
        var doc = XDocument.Load(targetsPath);

        var targetNames = doc.Descendants().Where(e => e.Name.LocalName == "Target")
            .Select(e => e.Attribute("Name")?.Value).ToList();
        Assert.Contains("FuseEmitCaptureFragment", targetNames);

        var text = File.ReadAllText(targetsPath);
        Assert.Contains("FuseCaptureEnabled", text);   // the opt-out property
        Assert.Contains("_FuseCaptureInner", text);    // the recursion guard on the nested build
        Assert.Contains("AfterTargets=\"Build\"", text); // fires after the build
    }
}
