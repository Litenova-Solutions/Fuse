namespace Fuse.Emission.Serialization;

/// <summary>
///     Machine-readable result of <c>fuse verify</c>: the preserved fraction of the public API surface.
/// </summary>
public sealed class JsonVerifyReportDto
{
    /// <summary>
    ///     Discriminator for verify records (<c>verify</c>).
    /// </summary>
    public string Type { get; set; } = "verify";

    /// <summary>
    ///     The analysis backend used (<c>roslyn</c> or <c>regex</c>).
    /// </summary>
    public string Backend { get; set; } = string.Empty;

    /// <summary>
    ///     The number of source files analyzed.
    /// </summary>
    public int Files { get; set; }

    /// <summary>
    ///     Public and protected type preservation.
    /// </summary>
    public JsonVerifyCategoryDto Types { get; set; } = new();

    /// <summary>
    ///     Public and protected method preservation.
    /// </summary>
    public JsonVerifyCategoryDto Methods { get; set; } = new();

    /// <summary>
    ///     Route template preservation.
    /// </summary>
    public JsonVerifyCategoryDto Routes { get; set; } = new();
}

/// <summary>
///     Per-category preservation counts and ratio.
/// </summary>
public sealed class JsonVerifyCategoryDto
{
    /// <summary>
    ///     The number of symbols found in the source.
    /// </summary>
    public int Total { get; set; }

    /// <summary>
    ///     The number of those symbols present in the fused output.
    /// </summary>
    public int Preserved { get; set; }

    /// <summary>
    ///     The preserved fraction, rounded to four places.
    /// </summary>
    public double Ratio { get; set; }
}
