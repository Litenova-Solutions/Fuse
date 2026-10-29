namespace Fuse.Testing.Model;

/// <summary>How the member walk ended: whether it finished, and what it cost.</summary>
/// <param name="Completed">False when the walk went past its symbol or time budget before finishing.</param>
/// <param name="Symbols">How many symbols the walk visited.</param>
/// <param name="Milliseconds">How long the walk ran.</param>
internal sealed record WalkOutcome(bool Completed, int Symbols, long Milliseconds);
