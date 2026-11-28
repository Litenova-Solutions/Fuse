namespace Fuse.Testing.Shadow;

/// <summary>A shadow file an emitter wrote: the 32-byte SHA-256 content hash, and the length and last write time the file had after.</summary>
internal sealed record ShadowWritten(byte[] ContentHash, long Length, DateTime WrittenUtc);
