namespace Fuse.Testing.Model;

/// <summary>
///     One test class whose tests all passed on inputs the key proves unchanged, kept so the next plan answers them
///     without running them.
/// </summary>
/// <param name="Project">Absolute path of the test project file.</param>
/// <param name="Class">The test class, as the test names before their last dot have it.</param>
/// <param name="Key">The input key the class passed with.</param>
/// <param name="Tests">The fully qualified names of the tests that passed, distinct and in ordinal order.</param>
/// <param name="StoredUtc">When the pass was stored.</param>
internal sealed record CachedPass(string Project, string Class, string Key, string[] Tests, DateTime StoredUtc);
