using Fuse.Scoping;

namespace Fuse.Fusion;

/// <summary>
///     Maps shared context-plan role strings to the fusion emission plan labels carried on
///     <see cref="Fuse.Emission.Models.PlannedFileInfo" />.
/// </summary>
internal static class FusionPlanRole
{
    /// <summary>Maps a shared plan role to the fusion result plan label.</summary>
    /// <param name="role">The role from <see cref="ContextPlanItem" />.</param>
    /// <returns>The label stored on <see cref="Fuse.Emission.Models.PlannedFileInfo" />.</returns>
    public static string ForEmission(string role) => role switch
    {
        "exact-seed" => "Seed",
        "dependency" => "Dependency",
        "changed" => "Changed",
        _ => role,
    };
}
