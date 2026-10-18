namespace Fuse.Graph;

/// <summary>
///     The test framework a test project's packages name, which decides how a run on Microsoft.Testing.Platform is
///     filtered: each framework spells its filter differently.
/// </summary>
internal enum TestFramework
{
    /// <summary>No package of a framework Fuse knows; a platform run of such a project runs whole.</summary>
    Unknown,

    /// <summary>xUnit v3, which filters with <c>--filter-method</c> and <c>--filter-class</c>.</summary>
    XUnit,

    /// <summary>MSTest, which accepts the VSTest filter syntax on the platform.</summary>
    MSTest,

    /// <summary>NUnit, which accepts the VSTest filter syntax on the platform.</summary>
    NUnit,

    /// <summary>TUnit, which filters with <c>--treenode-filter</c>.</summary>
    TUnit,
}
