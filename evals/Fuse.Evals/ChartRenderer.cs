using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Fuse.Evals;

/// <summary>
///     Renders <c>site/benefits.svg</c> from the newest correctness and selection results: Fuse's median time as a share
///     of the dotnet command it replaces, one bar per repository and operation, on one 0 to 100 percent axis.
/// </summary>
internal static class ChartRenderer
{
    private const int Width = 760;
    private const int LabelWidth = 250;
    private const int PlotLeft = LabelWidth + 10;
    private const int PlotRight = Width - 20;
    private const int RowHeight = 34;
    private const int BarHeight = 14;

    private sealed record Row(string Group, string Label, double Fuse, double Dotnet, string Unit);

    public static string Render(string fuseRoot)
    {
        var results = Path.Combine(fuseRoot, "evals", "results");
        var rows = new List<Row>();
        foreach (var (repo, label) in new[] { ("fixture", "Fixture, 5 projects"), ("NodaTime", "NodaTime, 17 projects") })
        {
            if (Latest(results, $"correctness-{repo}-") is { } correctness)
                rows.Add(new Row("check", label, correctness.GetProperty("fuseMedianMs").GetDouble() / 1000, correctness.GetProperty("buildMedianSeconds").GetDouble(), "s"));
        }

        foreach (var (repo, label) in new[] { ("fixture", "Fixture, 5 projects"), ("NodaTime", "NodaTime, 42,681 tests") })
        {
            if (Latest(results, $"selection-{repo}-") is { } selection)
                rows.Add(new Row("test", label, selection.GetProperty("fuseMedianSeconds").GetDouble(), selection.GetProperty("dotnetMedianSeconds").GetDouble(), "s"));
        }

        var groups = new[]
        {
            ("check", "Check an edit: fuse check vs dotnet build"),
            ("test", "Run the affected tests: fuse test vs dotnet test"),
        };
        var height = 96 + groups.Length * 30 + rows.Count * RowHeight + 30;
        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {Width} {height}" width="{Width}" height="{height}" role="img" aria-labelledby="t d">
            <title id="t">Fuse time as a share of the dotnet command it replaces</title>
            <desc id="d">{Description(rows)}</desc>
            <style>
              .bg {'{'} fill: #fcfcfb; {'}'}
              .title {'{'} fill: #0b0b0b; font: 600 17px system-ui, -apple-system, 'Segoe UI', sans-serif; {'}'}
              .sub, .axis, .val {'{'} fill: #52514e; font: 13px system-ui, -apple-system, 'Segoe UI', sans-serif; {'}'}
              .group {'{'} fill: #0b0b0b; font: 600 14px system-ui, -apple-system, 'Segoe UI', sans-serif; {'}'}
              .label {'{'} fill: #0b0b0b; font: 14px system-ui, -apple-system, 'Segoe UI', sans-serif; {'}'}
              .val {'{'} font-variant-numeric: tabular-nums; {'}'}
              .val-in {'{'} fill: #ffffff; font: 600 13px system-ui, -apple-system, 'Segoe UI', sans-serif; font-variant-numeric: tabular-nums; {'}'}
              .grid {'{'} stroke: #e2e1dc; stroke-width: 1; {'}'}
              .ref {'{'} stroke: #8a8984; stroke-width: 1.5; stroke-dasharray: 4 3; {'}'}
              .bar {'{'} fill: #2a78d6; {'}'}
              @media (prefers-color-scheme: dark) {'{'}
                .bg {'{'} fill: #1a1a19; {'}'}
                .title, .group, .label {'{'} fill: #ffffff; {'}'}
                .sub, .axis, .val {'{'} fill: #c3c2b7; {'}'}
                .grid {'{'} stroke: #34332f; {'}'}
                .ref {'{'} stroke: #8f8e87; {'}'}
                .bar {'{'} fill: #3987e5; {'}'}
              {'}'}
            </style>
            <rect class="bg" width="{Width}" height="{height}" rx="10"/>
            <text class="title" x="20" y="34">Time until the agent knows</text>
            <text class="sub" x="20" y="56">Fuse's median time as a share of the dotnet command it replaces. Shorter is better.</text>

            """);

        var plotTop = 80;
        var plotBottom = height - 36;
        foreach (var tick in new[] { 0, 25, 50, 75, 100 })
        {
            var x = X(tick / 100.0);
            svg.Append(CultureInfo.InvariantCulture, $"""<line class="{(tick == 100 ? "ref" : "grid")}" x1="{x:0.#}" y1="{plotTop}" x2="{x:0.#}" y2="{plotBottom}"/>""").Append('\n');
            svg.Append(CultureInfo.InvariantCulture, $"""<text class="axis" x="{x:0.#}" y="{plotBottom + 18}" text-anchor="middle">{tick}%</text>""").Append('\n');
        }

        svg.Append(CultureInfo.InvariantCulture, $"""<text class="axis" x="{X(1) - 6:0.#}" y="{plotTop + 12}" text-anchor="end">dotnet = 100%</text>""").Append('\n');

        var y = plotTop + 8;
        foreach (var (key, title) in groups)
        {
            y += 22;
            svg.Append(CultureInfo.InvariantCulture, $"""<text class="group" x="20" y="{y}">{title}</text>""").Append('\n');
            y += 8;
            foreach (var row in rows.Where(r => r.Group == key))
            {
                var share = Math.Min(1, row.Fuse / row.Dotnet);
                var barY = y + (RowHeight - BarHeight) / 2;
                var barEnd = X(share);
                svg.Append(CultureInfo.InvariantCulture, $"""<text class="label" x="32" y="{barY + BarHeight - 2}">{row.Label}</text>""").Append('\n');
                svg.Append(CultureInfo.InvariantCulture, $"""<path class="bar" d="{BarPath(PlotLeft, barY, barEnd - PlotLeft, BarHeight)}"><title>{row.Label}: {Seconds(row.Fuse)} vs {Seconds(row.Dotnet)}</title></path>""").Append('\n');
                // The label sits after the bar, or inside it when it would run into the 100 percent line.
                var label = string.Create(CultureInfo.InvariantCulture, $"{share * 100:0}%  ({Seconds(row.Fuse)} vs {Seconds(row.Dotnet)})");
                var inside = barEnd + 8 + (label.Length * 7) > PlotRight - 6;
                svg.Append(inside
                    ? string.Create(CultureInfo.InvariantCulture, $"""<text class="val-in" x="{barEnd - 8:0.#}" y="{barY + BarHeight - 2}" text-anchor="end">{label}</text>""")
                    : string.Create(CultureInfo.InvariantCulture, $"""<text class="val" x="{barEnd + 8:0.#}" y="{barY + BarHeight - 2}">{label}</text>""")).Append('\n');
                y += RowHeight;
            }
        }

        svg.Append("</svg>\n");
        return svg.ToString();
    }

    private static double X(double share) => PlotLeft + share * (PlotRight - PlotLeft);

    /// <summary>A bar anchored square at the baseline with 4px rounded data end.</summary>
    private static string BarPath(double x, double y, double width, double height)
    {
        var r = Math.Min(4, width / 2);
        return string.Create(CultureInfo.InvariantCulture, $"M{x:0.#},{y:0.#}h{width - r:0.#}a{r:0.#},{r:0.#} 0 0 1 {r:0.#},{r:0.#}v{height - 2 * r:0.#}a{r:0.#},{r:0.#} 0 0 1 -{r:0.#},{r:0.#}h-{width - r:0.#}z");
    }

    private static string Seconds(double value) =>
        value < 10 ? value.ToString("0.00", CultureInfo.InvariantCulture) + " s" : value.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    private static string Description(IEnumerable<Row> rows) =>
        string.Join("; ", rows.Select(r => $"{(r.Group == "check" ? "check" : "tests")}, {r.Label}: {Seconds(r.Fuse)} with Fuse against {Seconds(r.Dotnet)} with dotnet"));

    private static JsonElement? Latest(string directory, string prefix)
    {
        var file = Directory.Exists(directory)
            ? Directory.GetFiles(directory, prefix + "*.json").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault()
            : null;
        if (file is null)
            return null;
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        return document.RootElement.Clone();
    }
}
