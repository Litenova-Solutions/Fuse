namespace Fuse.Testing.Model;

/// <summary>A target framework as a build output folder names it: net9.0, netstandard2.0, net481.</summary>
/// <param name="Family">Which runtime family the framework belongs to.</param>
/// <param name="Version">The framework version, compared numerically.</param>
internal sealed record TargetFramework(TargetFramework.FamilyKind Family, Version Version)
{
    /// <summary>Which runtime family a target framework belongs to.</summary>
    internal enum FamilyKind
    {
        /// <summary>.NET 5 and later.</summary>
        Net,

        /// <summary>.NET Standard.</summary>
        NetStandard,

        /// <summary>.NET Framework.</summary>
        NetFramework,
    }

    /// <summary>The highest .NET Standard a .NET 5 or later build loads.</summary>
    private static readonly Version MaxNetStandardForNet = new(2, 1);

    /// <summary>The highest .NET Standard a .NET Framework build loads.</summary>
    private static readonly Version MaxNetStandardForNetFramework = new(2, 0);

    /// <summary>
    ///     The framework a build output folder names, or null when it names none. Under central output the folder
    ///     prefixes the framework with the configuration (<c>debug_net9.0</c>); a runtime identifier folder
    ///     (<c>win-x64</c>) names none.
    /// </summary>
    public static TargetFramework? ParseFolder(string folder)
    {
        var text = folder.Contains('_') ? folder[(folder.LastIndexOf('_') + 1)..] : folder;
        return Parse(text);
    }

    /// <summary>The framework <paramref name="text"/> names, without a platform suffix, or null.</summary>
    public static TargetFramework? Parse(string text)
    {
        // A platform suffix reads as its framework without it: net8.0-windows is net8.0.
        var end = 0;
        while (end < text.Length && (char.IsAsciiLetter(text[end]) || char.IsAsciiDigit(text[end]) || text[end] == '.'))
            end++;
        text = text[..end];
        if (text.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase)
            && Version.TryParse(text["netstandard".Length..], out var standard)
            && standard is not null)
            return new TargetFramework(FamilyKind.NetStandard, standard);
        if (text.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase)
            && Version.TryParse(text["netcoreapp".Length..], out var core)
            && core is not null)
            return new TargetFramework(FamilyKind.Net, core);
        if (!text.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            return null;
        var rest = text["net".Length..];
        if (rest.Contains('.'))
            return Version.TryParse(rest, out var net) && net is not null ? new TargetFramework(FamilyKind.Net, net) : null;
        // A .NET Framework moniker spells its version without dots: net462 is 4.6.2.
        if (rest.Length is >= 1 and <= 4 && rest[0] == '4' && rest.All(char.IsAsciiDigit))
        {
            var parts = rest.Select(c => c - '0').ToArray();
            var version = parts.Length switch
            {
                1 => new Version(parts[0], 0),
                2 => new Version(parts[0], parts[1]),
                3 => new Version(parts[0], parts[1], parts[2]),
                _ => new Version(parts[0], parts[1], parts[2], parts[3]),
            };
            return new TargetFramework(FamilyKind.NetFramework, version);
        }

        return null;
    }

    /// <summary>
    ///     Whether a build for this framework loads a dependency built for <paramref name="candidate"/>, as MSBuild
    ///     resolves project references: the same family at this version or below, or .NET Standard at the highest
    ///     version this family loads. A higher version never loads: its reference assemblies are not on the runtime.
    /// </summary>
    public bool Accepts(TargetFramework candidate) => Family switch
    {
        FamilyKind.Net => (candidate.Family == FamilyKind.Net && candidate.Version <= Version)
            || (candidate.Family == FamilyKind.NetStandard && candidate.Version <= MaxNetStandardForNet),
        FamilyKind.NetStandard => candidate.Family == FamilyKind.NetStandard && candidate.Version <= Version,
        FamilyKind.NetFramework => (candidate.Family == FamilyKind.NetFramework && candidate.Version <= Version)
            || (candidate.Family == FamilyKind.NetStandard && candidate.Version <= MaxNetStandardForNetFramework),
        _ => false,
    };
}
