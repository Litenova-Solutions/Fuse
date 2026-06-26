using System.Text;
using System.Text.Json;

namespace Fuse.Benchmarks;

/// <summary>
///     Renders a <see cref="SuiteResult" /> as a human-readable scorecard and persists it as JSON to the
///     results directory (the single source of truth for quoted numbers, Section 18.11).
/// </summary>
public static class Reporting
{
    /// <summary>
    ///     Formats a suite result as a compact text scorecard.
    /// </summary>
    /// <param name="result">The suite result.</param>
    /// <returns>The formatted scorecard.</returns>
    public static string FormatScorecard(SuiteResult result)
    {
        var card = result.Scorecard;
        var builder = new StringBuilder();
        builder.AppendLine($"Fuse Eval: {result.Suite}");
        builder.AppendLine(result.Description);
        builder.AppendLine($"  tasks:     {card.TaskCount}");
        builder.AppendLine($"  recall:    {card.Recall:P1}  (95% CI {card.RecallCiLow:P0}-{card.RecallCiHigh:P0})");
        builder.AppendLine($"  precision: {card.Precision:P1}");
        builder.AppendLine($"  F1:        {card.F1:F2}");
        if (card.MeanTokens > 0)
            builder.AppendLine($"  tokens:    median {card.MedianTokens:N0}  mean {card.MeanTokens:N0}");
        if (card.LowSignalF1 > 0)
            builder.AppendLine($"  low-signal detection F1: {card.LowSignalF1:F2}");
        foreach (var note in result.Notes)
            builder.AppendLine($"  note: {note}");
        return builder.ToString();
    }

    /// <summary>
    ///     Serializes a suite result to JSON.
    /// </summary>
    /// <param name="result">The suite result.</param>
    /// <returns>The indented JSON text.</returns>
    public static string ToJson(SuiteResult result)
        => JsonSerializer.Serialize(result, BenchmarkJsonContext.Default.SuiteResult);

    /// <summary>
    ///     Writes a suite result to the given path, creating the directory if needed.
    /// </summary>
    /// <param name="result">The suite result.</param>
    /// <param name="path">The destination file path.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    public static async Task WriteAsync(SuiteResult result, string path, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, ToJson(result), cancellationToken);
    }
}
