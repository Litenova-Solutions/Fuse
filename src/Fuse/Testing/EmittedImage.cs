namespace Fuse.Testing;

/// <summary>One assembly emitted from a warm compilation, with its portable PDB, as <see cref="EmitCache"/> keeps it.</summary>
/// <param name="Pe">The assembly.</param>
/// <param name="Pdb">Its portable PDB.</param>
internal sealed record EmittedImage(byte[] Pe, byte[] Pdb);
