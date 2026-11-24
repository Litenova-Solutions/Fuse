namespace Fuse.Testing.Model;

/// <summary>One emitted assembly image passed to a warm test worker for collectible ALC loading.</summary>
/// <param name="SimpleName">Assembly simple name without extension.</param>
/// <param name="Pe">Emitted PE image bytes.</param>
/// <param name="Pdb">Emitted portable PDB bytes.</param>
internal sealed record WarmAssemblyImage(string SimpleName, byte[] Pe, byte[] Pdb);
