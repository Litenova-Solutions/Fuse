namespace Fuse.Harnesses;

/// <summary>
///     The events <c>fuse hook</c> answers, as the command line names them. <see cref="Harness.RegisterHooks"/> writes
///     these names into users' settings and <c>opencode-plugin.js</c> passes them, so renaming one leaves every existing
///     registration calling a name <c>fuse hook</c> does not accept until the user reruns <c>fuse init</c>.
/// </summary>
internal static class HookEvent
{
    /// <summary>Before the agent runs a shell command. Fuse rewrites <c>dotnet build</c> and <c>dotnet test</c> in it to their Fuse equivalents.</summary>
    public const string PreShell = "pre-shell";

    /// <summary>After an edit tool wrote files. Fuse checks the source files it wrote.</summary>
    public const string PostEdit = "post-edit";

    /// <summary>Before the agent finishes. Fuse checks every change and sends the agent back while errors are introduced.</summary>
    public const string Stop = "stop";
}
