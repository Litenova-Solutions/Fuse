namespace Fuse.GoldenOutput.Tests;

internal static class GoldenOutputAssert
{
    public static void AssertMatches(string scenarioName, string actual)
    {
        var expectedPath = Path.Combine(GoldenPaths.ExpectedDirectory, scenarioName + ".golden");

        if (string.Equals(Environment.GetEnvironmentVariable("UPDATE_GOLDEN_FILES"), "1", StringComparison.Ordinal))
        {
            var sourceExpected = Path.Combine(
                GoldenPaths.RepoRoot,
                "tests",
                "Fuse.GoldenOutput.Tests",
                "expected",
                scenarioName + ".golden");
            Directory.CreateDirectory(Path.GetDirectoryName(sourceExpected)!);
            File.WriteAllText(sourceExpected, actual, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Directory.CreateDirectory(GoldenPaths.ExpectedDirectory);
            File.WriteAllText(expectedPath, actual, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;
        }

        Assert.True(File.Exists(expectedPath), $"Golden file missing: {expectedPath}. Set UPDATE_GOLDEN_FILES=1 to generate.");
        var expected = File.ReadAllText(expectedPath);
        Assert.Equal(expected, actual);
    }
}
