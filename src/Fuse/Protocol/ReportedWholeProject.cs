namespace Fuse.Protocol;

/// <summary>Why a project was bound whole, as the client receives it.</summary>
/// <param name="Project">The name of the project that was bound whole.</param>
/// <param name="ChangedFiles">Repository-relative paths of the changed files that forced it.</param>
/// <param name="Causes">The declaration changes that forced it, each as <c>removed: ...</c> or <c>changed: ...</c>.</param>
internal sealed record ReportedWholeProject(string Project, string[] ChangedFiles, string[] Causes);
