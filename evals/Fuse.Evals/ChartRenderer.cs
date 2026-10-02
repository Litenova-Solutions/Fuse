using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;

namespace Fuse.Evals;

/// <summary>
///     Renders <c>site/assets/benefits.svg</c>, the chart the README and the results page show, from the newest correctness and
///     selection result of every pinned open-source repository. It has two panels: <c>fuse check</c> against <c>dotnet build</c>, and
///     <c>fuse test</c> against <c>dotnet test</c>. Each panel has one cell per repository, drawn like the landing page's
///     lanes: the dotnet command at full length, Fuse at its median time as a share of that, each with its time, and under
///     them how many times faster Fuse is, then for each command the range from its fastest to its slowest case, its mean
///     and its P95, so the median is not shown without the spread behind it. A header names Fuse with its logo, what it
///     is, its website and repository, and the last lines name the version and date measured and where every number is
///     documented, so the chart still says what it is when it is shared on its own. Every number is read from the result files, so the chart and the landing page
///     agree as long as both are updated from the same files. The colors and fonts are the site's; a dark variant applies
///     when the viewer asks for one.
/// </summary>
internal static class ChartRenderer
{
    private const int Width = 800;
    private const int PadX = 36;
    private const int ColumnGap = 56;
    private const int CellWidth = (Width - 2 * PadX - ColumnGap) / 2;
    private const int LabelWidth = 96;
    private const int TimeWidth = 56;
    private const int LaneGap = 12;
    private const int TrackHeight = 10;
    private const int TrackRadius = 4;
    private const int TrackWidth = CellWidth - LabelWidth - TimeWidth - 2 * LaneGap;

    // Vertical distances, all in the SVG's own units. A panel starts at its top, and a cell starts at the baseline of
    // its repository name.
    private const int TopMargin = 40;
    private const int HeaderHeight = 92;
    private const int PanelSubtitleBaseline = 46;
    private const int PanelGridTop = 80;
    private const int FirstLaneCenter = 25;
    private const int LaneSpacing = 22;
    private const int SpeedupBaseline = 77;
    private const int StatsBaseline = 97;
    private const int StatsSpacing = 17;
    private const int CellHeight = 118;
    private const int RowPitch = 146;
    private const int PanelSpacing = 26;
    private const int NoteOffset = 42;
    private const int BottomMargin = 26;

    private const string Style = """
          .bg { fill: #fcfcfb; stroke: #e4e3de; }
          .brand { font-size: 24px; font-weight: 700; letter-spacing: -0.3px; }
          .tagline { font-size: 13.5px; fill: #5b5a55; }
          .site { font-size: 14px; font-weight: 600; fill: #6d4aff; }
          .repo-url { font-size: 12.5px; fill: #5b5a55; }
          .rule { stroke: #e4e3de; }
          text { font-family: ui-sans-serif, system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; fill: #0b0b0b; }
          .mono { font-family: ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace; }
          .panel { font-size: 21px; font-weight: 700; letter-spacing: -0.3px; }
          .sub { font-size: 14px; fill: #5b5a55; }
          .sub .mono { font-size: 13px; }
          .repo { font-size: 15px; font-weight: 600; }
          .meta, .note { font-size: 13px; fill: #5b5a55; }
          .note { font-size: 12.5px; }
          .cmd, .t { font-size: 13.5px; fill: #5b5a55; }
          .t { font-variant-numeric: tabular-nums; }
          .fuse-text { fill: #0b0b0b; font-weight: 600; }
          .speed { font-size: 13px; fill: #5b5a55; }
          .speed tspan { fill: #0b0b0b; font-weight: 600; }
          .stats { font-size: 11.5px; fill: #5b5a55; font-variant-numeric: tabular-nums; }
          .track { fill: #ebeae6; }
          .fill-slow { fill: #b4b3ad; }
          .fill-fuse { fill: #6d4aff; }
          @media (prefers-color-scheme: dark) {
            .bg { fill: #1a1a19; stroke: #34332f; }
            .tagline, .repo-url { fill: #b9b8af; }
            .site { fill: #9b85ff; }
            .rule { stroke: #34332f; }
            text, .fuse-text, .speed tspan { fill: #f4f4f0; }
            .sub, .meta, .note, .cmd, .t, .speed, .stats { fill: #b9b8af; }
            .fuse-text { fill: #f4f4f0; }
            .track { fill: #2e2d2a; }
            .fill-slow { fill: #6f6e68; }
            .fill-fuse { fill: #9b85ff; }
          }
        """;

    private sealed record Panel(string Key, string Title, string FuseCommand, string DotnetCommand);

    private sealed record Row(Panel Panel, string Repo, string Detail, double Fuse, double Dotnet, CaseStats FuseStats, CaseStats DotnetStats)
    {
        public double Speedup => Dotnet / Fuse;
    }

    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly Panel Check = new("check", "Check an edit", "fuse check", "dotnet build");
    private static readonly Panel Test = new("test", "Run the affected tests", "fuse test", "dotnet test");
    private static readonly Panel[] Panels = [Check, Test];

    public static string Render(string fuseRoot)
    {
        var rows = ReadRows(Path.Combine(fuseRoot, "evals", "results"));
        var body = new StringBuilder();
        AppendHeader(body);
        var y = HeaderHeight + PanelSpacing;
        var drawn = 0;
        foreach (var panel in Panels)
        {
            var panelRows = rows.Where(r => r.Panel == panel).ToList();
            if (panelRows.Count == 0)
                continue;

            if (drawn++ > 0)
            {
                y += PanelSpacing;
                body.Append(CultureInfo.InvariantCulture, $"""<line class="rule" x1="{PadX}" y1="{y}" x2="{Width - PadX}" y2="{y}"/>""").Append('\n');
                y += PanelSpacing;
            }

            body.Append(CultureInfo.InvariantCulture, $"""<text class="panel" x="{PadX}" y="{y + 22}">{Escape(panel.Title)}</text>""").Append('\n');
            body.Append(CultureInfo.InvariantCulture, $"""<text class="sub" x="{PadX}" y="{y + PanelSubtitleBaseline}"><tspan class="mono">{Escape(panel.FuseCommand)}</tspan> vs <tspan class="mono">{Escape(panel.DotnetCommand)}</tspan></text>""").Append('\n');

            var gridTop = y + PanelGridTop;
            for (var i = 0; i < panelRows.Count; i++)
            {
                // A last cell alone on its row is centered under the two above it.
                var alone = i == panelRows.Count - 1 && i % 2 == 0;
                var x = alone ? (Width - CellWidth) / 2 : PadX + (i % 2) * (CellWidth + ColumnGap);
                AppendCell(body, panelRows[i], x, gridTop + (i / 2) * RowPitch);
            }

            y = gridTop + ((panelRows.Count - 1) / 2) * RowPitch + CellHeight;
        }

        var noteY = y + NoteOffset;
        body.Append(CultureInfo.InvariantCulture, $"""<text class="note" x="{PadX}" y="{noteY}">Bars are medians over the eval cases: each dotnet bar is 100 percent, each Fuse bar its share of it.</text>""").Append('\n');
        body.Append(CultureInfo.InvariantCulture, $"""<text class="note" x="{PadX}" y="{noteY + 18}">Under them: fastest to slowest case, mean and P95. On GitHub Actions windows-latest runners.</text>""").Append('\n');
        body.Append(CultureInfo.InvariantCulture, $"""<text class="note" x="{PadX}" y="{noteY + 36}">{Escape(Provenance(Path.Combine(fuseRoot, "evals", "results")))} Every number and how it was measured: fuse.codes/docs/benchmarks</text>""").Append('\n');
        noteY += 36;
        var height = noteY + BottomMargin;

        return string.Create(CultureInfo.InvariantCulture, $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {Width} {height}" width="{Width}" height="{height}" role="img" aria-labelledby="t d">
            <title id="t">Fuse compared with dotnet build and dotnet test (fuse.codes)</title>
            <desc id="d">{Escape(Description(rows))}</desc>
            <style>
            {Style}
            </style>
            <rect class="bg" x="0.5" y="0.5" width="{Width - 1}" height="{height - 1}" rx="12"/>
            {body}</svg>

            """);
    }

    /// <summary>The Fuse logo and name, what Fuse is, and its website and repository, above a rule.</summary>
    private static void AppendHeader(StringBuilder svg)
    {
        svg.Append(CultureInfo.InvariantCulture, $"""<g transform="translate({PadX},26) scale(1.25)">{Logo}</g>""").Append('\n');
        svg.Append(CultureInfo.InvariantCulture, $"""<text class="brand" x="{PadX + 52}" y="48">Fuse</text>""").Append('\n');
        svg.Append(CultureInfo.InvariantCulture, $"""<text class="tagline" x="{PadX + 52}" y="68">Faster .NET build and test loop for AI coding agents</text>""").Append('\n');
        svg.Append(CultureInfo.InvariantCulture, $"""<a href="https://fuse.codes"><text class="site" x="{Width - PadX}" y="46" text-anchor="end">fuse.codes</text></a>""").Append('\n');
        svg.Append(CultureInfo.InvariantCulture, $"""<a href="https://github.com/Litenova-Solutions/Fuse"><text class="repo-url" x="{Width - PadX}" y="66" text-anchor="end">github.com/Litenova-Solutions/Fuse</text></a>""").Append('\n');
        svg.Append(CultureInfo.InvariantCulture, $"""<line class="rule" x1="{PadX}" y1="{HeaderHeight}" x2="{Width - PadX}" y2="{HeaderHeight}"/>""").Append('\n');
    }

    /// <summary>The site's icon, site/assets/fuse-icon.svg, drawn in a 32 by 32 box.</summary>
    private const string Logo = """<rect width="32" height="32" rx="7" fill="#6d4aff"/><g fill="none" stroke="#ffffff" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" opacity="0.6"><path d="M10 7c-2.5 0-3 1.2-3 3.5v2.3c0 1.8-1 3.2-2.5 3.2 1.5 0 2.5 1.4 2.5 3.2v2.3C7 23.8 7.5 25 10 25"/><path d="M22 7c2.5 0 3 1.2 3 3.5v2.3c0 1.8 1 3.2 2.5 3.2-1.5 0-2.5 1.4-2.5 3.2v2.3c0 2.3-.5 3.5-3 3.5"/></g><path d="M17.8 7.5 11.5 17.2h4.3l-1.6 7.3 6.3-9.7h-4.3z" fill="#ffffff" stroke="#ffffff" stroke-width="0.8" stroke-linejoin="round"/>""";

    /// <summary>
    ///     "Fuse 5.2.2, measured 2026-10-02.": the version of the build the newest correctness file records, and the date in
    ///     its name.
    /// </summary>
    private static string Provenance(string results)
    {
        var file = PinnedRepo.All
            .Select(r => Directory.Exists(results) ? Directory.GetFiles(results, $"correctness-{r.Name}-*.json").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault() : null)
            .OfType<string>()
            .OrderBy(f => Path.GetFileNameWithoutExtension(f)[^13..], StringComparer.Ordinal)
            .LastOrDefault();
        if (file is null)
            return "";
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var version = document.RootElement.GetProperty("fuseBuild").GetString()!.Split('/')[0];
        var stamp = Path.GetFileNameWithoutExtension(file)[^13..^5];
        return $"Fuse {version}, measured {stamp[..4]}-{stamp[4..6]}-{stamp[6..]}.";
    }

    private static void AppendCell(StringBuilder svg, Row row, int x, int y)
    {
        var share = Math.Min(1, row.Fuse / row.Dotnet);
        var trackLeft = x + LabelWidth + LaneGap;
        var right = x + CellWidth;

        svg.Append(CultureInfo.InvariantCulture, $"""<text class="repo" x="{x}" y="{y}">{Escape(row.Repo)}</text>""").Append('\n');
        if (row.Detail.Length > 0)
            svg.Append(CultureInfo.InvariantCulture, $"""<text class="meta" x="{right}" y="{y}" text-anchor="end">{Escape(row.Detail)}</text>""").Append('\n');

        var fuseCenter = y + FirstLaneCenter;
        var dotnetCenter = fuseCenter + LaneSpacing;
        AppendLane(svg, row.Panel.FuseCommand, Seconds(row.Fuse), fuseCenter, x, trackLeft, right, share, fuse: true);
        AppendLane(svg, row.Panel.DotnetCommand, Seconds(row.Dotnet), dotnetCenter, x, trackLeft, right, 1, fuse: false);

        // The speedup is the ratio of the two medians, so it is computed from the results and not from the rounded times.
        var faster = row.Speedup >= 1;
        var text = Times(faster ? row.Speedup : 1 / row.Speedup);
        svg.Append(CultureInfo.InvariantCulture, $"""<text class="speed" x="{x}" y="{y + SpeedupBaseline}"><tspan>{text}</tspan> {(faster ? "faster" : "slower")} at the median</text>""").Append('\n');
        svg.Append(CultureInfo.InvariantCulture, $"""<text class="stats" x="{x}" y="{y + StatsBaseline}">Fuse: {Spread(row.FuseStats)}</text>""").Append('\n');
        svg.Append(CultureInfo.InvariantCulture, $"""<text class="stats" x="{x}" y="{y + StatsBaseline + StatsSpacing}">dotnet: {Spread(row.DotnetStats)}</text>""").Append('\n');
    }

    /// <summary>"0.05 to 13.1 s, mean 1.36 s, P95 9.73 s": the range, the mean and P95 of one command's cases.</summary>
    private static string Spread(CaseStats stats) =>
        $"{Number(stats.Fastest)} to {Seconds(stats.Slowest)}, mean {Seconds(stats.Mean)}, P95 {Seconds(stats.P95)}";

    private static void AppendLane(StringBuilder svg, string command, string time, int center, int x, int trackLeft, int right, double share, bool fuse)
    {
        var baseline = center + 4.5;
        var top = center - TrackHeight / 2;
        svg.Append(CultureInfo.InvariantCulture, $"""<text class="cmd mono{(fuse ? " fuse-text" : "")}" x="{x}" y="{baseline:0.#}">{Escape(command)}</text>""").Append('\n');
        if (fuse)
            svg.Append(CultureInfo.InvariantCulture, $"""<path class="track" d="{BarPath(trackLeft, top, TrackWidth)}"/>""").Append('\n');
        svg.Append(CultureInfo.InvariantCulture, $"""<path class="{(fuse ? "fill-fuse" : "fill-slow")}" d="{BarPath(trackLeft, top, TrackWidth * share)}"/>""").Append('\n');
        svg.Append(CultureInfo.InvariantCulture, $"""<text class="t{(fuse ? " fuse-text" : "")}" x="{right}" y="{baseline:0.#}" text-anchor="end">{time}</text>""").Append('\n');
    }

    /// <summary>A bar with a square left end at the baseline and a rounded right end, like the landing page's lanes.</summary>
    private static string BarPath(double x, double y, double width)
    {
        var r = Math.Min(TrackRadius, width / 2);
        return string.Create(CultureInfo.InvariantCulture, $"M{x:0.##},{y:0.##}h{width - r:0.##}a{r:0.##},{r:0.##} 0 0 1 {r:0.##},{r:0.##}v{TrackHeight - 2 * r:0.##}a{r:0.##},{r:0.##} 0 0 1 -{r:0.##},{r:0.##}h-{width - r:0.##}z");
    }

    private static List<Row> ReadRows(string results)
    {
        var rows = new List<Row>();
        foreach (var repo in PinnedRepo.All)
        {
            if (Latest(results, $"correctness-{repo.Name}-") is { } correctness)
            {
                var (name, detail) = SplitLabel(repo.CheckLabel);
                rows.Add(new Row(Check, name, detail, correctness.GetProperty("fuseMedianMs").GetDouble() / 1000, correctness.GetProperty("buildMedianSeconds").GetDouble(),
                    Stats(correctness, "fuseStats", "fuseMilliseconds", 1000), Stats(correctness, "dotnetStats", "buildSeconds", 1)));
            }

            if (Latest(results, $"selection-{repo.Name}-") is { } selection)
            {
                // The test panel gives every repository's size as its test count, taken from the result file it draws:
                // the tests in a full run at HEAD. The label's own size is only for a file that does not record it.
                var (name, detail) = SplitLabel(repo.TestLabel);
                if (selection.TryGetProperty("totalTests", out var total))
                    detail = string.Create(CultureInfo.InvariantCulture, $"{total.GetInt32():N0} tests");
                rows.Add(new Row(Test, name, detail, selection.GetProperty("fuseMedianSeconds").GetDouble(), selection.GetProperty("dotnetMedianSeconds").GetDouble(),
                    Stats(selection, "fuseStats", "fuseSeconds", 1), Stats(selection, "dotnetStats", "dotnetSeconds", 1)));
            }
        }

        return rows;
    }

    /// <summary>
    ///     The spread of one command's times: the result file's <paramref name="field"/>, or for a file written before it
    ///     had one, the same statistics computed from each case's <paramref name="perCase"/> divided by
    ///     <paramref name="divisor"/>.
    /// </summary>
    private static CaseStats Stats(JsonElement result, string field, string perCase, double divisor)
    {
        if (result.TryGetProperty(field, out var stats))
            return stats.Deserialize<CaseStats>(CamelCase)!;
        return CaseStats.Of(result.GetProperty("details").EnumerateArray().Select(c => c.GetProperty(perCase).GetDouble() / divisor));
    }

    /// <summary>A label is the repository's name, then a comma and a space, then its size ("NodaTime, 15 projects").</summary>
    private static (string Name, string Detail) SplitLabel(string label)
    {
        var comma = label.LastIndexOf(", ", StringComparison.Ordinal);
        return comma < 0 ? (label, "") : (label[..comma], label[(comma + 2)..]);
    }

    /// <summary>Seconds rounded half up: two decimals below 10 s and one decimal from 10 s.</summary>
    private static string Number(double value)
    {
        var rounded = Math.Round((decimal)value, 2, MidpointRounding.AwayFromZero);
        return rounded < 10
            ? rounded.ToString("0.00", CultureInfo.InvariantCulture)
            : Math.Round((decimal)value, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static string Seconds(double value) => Number(value) + " s";

    private static string Times(double value) =>
        Math.Round((decimal)value, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture) + "x";

    /// <summary>The chart's text for a reader who cannot see it: every time and every speedup, in words.</summary>
    private static string Description(IReadOnlyList<Row> rows)
    {
        var sentences = new List<string>();
        foreach (var panel in Panels)
        {
            var cells = rows.Where(r => r.Panel == panel).Select(r =>
            {
                var faster = r.Speedup >= 1;
                var times = Times(faster ? r.Speedup : 1 / r.Speedup);
                var name = r.Detail.Length > 0 ? $"{r.Repo} ({r.Detail})" : r.Repo;
                return $"{name}: {Words(r.Fuse)} with Fuse, {Words(r.Dotnet)} with dotnet, {times} {(faster ? "faster" : "slower")} at the median; Fuse {Spread(r.FuseStats)}; dotnet {Spread(r.DotnetStats)}";
            }).ToList();
            if (cells.Count > 0)
                sentences.Add($"{panel.Title}, {panel.FuseCommand} against {panel.DotnetCommand}. {string.Join("; ", cells)}.");
        }

        sentences.Insert(0, "Fuse, a faster .NET build and test loop for AI coding agents (fuse.codes, github.com/Litenova-Solutions/Fuse).");
        sentences.Add("Median times over the eval cases, each with the range from the fastest to the slowest case, the mean and P95, on GitHub Actions windows-latest runners.");
        return string.Join(' ', sentences);
    }

    private static string Words(double value) => Number(value) + " seconds";

    private static string Escape(string text) => SecurityElement.Escape(text) ?? "";

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
