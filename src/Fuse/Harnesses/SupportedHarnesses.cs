namespace Fuse.Harnesses;

/// <summary>Every harness Fuse supports, and the lookup from the name on the command line to its <see cref="Harness"/>.</summary>
internal static class SupportedHarnesses
{
    /// <summary>One instance of each harness, in the order <c>fuse init</c> registers them and the usage line lists them.</summary>
    public static IReadOnlyList<Harness> All { get; } = [new ClaudeCode(), new Cursor(), new GeminiCli(), new Codex(), new CopilotCli(), new OpenCode()];

    /// <summary>The harness whose <see cref="Harness.Name"/> is <paramref name="name"/>, compared exactly, or null for any other name.</summary>
    public static Harness? Find(string name) => All.FirstOrDefault(h => h.Name == name);
}
