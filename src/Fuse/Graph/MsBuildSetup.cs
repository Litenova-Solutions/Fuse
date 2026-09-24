using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace Fuse.Graph;

/// <summary>Points the process at the .NET SDK's MSBuild. Must run before any Microsoft.Build type loads.</summary>
internal static class MsBuildSetup
{
    private static readonly Lock Gate = new();

    /// <summary>Registers the SDK selected by the repository's global.json (the working directory must be the repository root).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (MSBuildLocator.IsRegistered)
                return;
            MSBuildLocator.RegisterDefaults();
        }
    }
}
