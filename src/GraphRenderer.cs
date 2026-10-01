using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace UdpPingGraph;

/// <summary>
/// Renders the current engine state as a status panel plus a scrolling
/// latency graph, one column per display tick, one character per row.
/// </summary>
internal static class GraphRenderer
{
    private const int GraphHeight = 10;
    private const int LabelWidth = 6;

    public static IRenderable Render(PingEngine engine, CliOptions options, int consoleWidth)
    {
        // Reserve room for the y-axis labels and the panels' own borders.
        var columns = Math.Max(10, consoleWidth - LabelWidth - 5);

        var currentTick = engine.CurrentTickIndex;
        var startTick = currentTick - columns + 1;

        var snapshots = new TickSnapshot[columns];
        engine.FillTickSnapshots(startTick, snapshots);

        var stats = AggregateStats.FromSnapshots(snapshots);
        var (scaleMin, scaleMax) = DetermineScale(stats);

        var grid = BuildGrid(snapshots, scaleMin, scaleMax);
        var graphMarkup = BuildGraphMarkup(grid, scaleMin, scaleMax);

        var graphPanel = new Panel(new Markup(graphMarkup))
            .Header(" latency (ms) ")
            .RoundedBorder().BorderColor(Color.Gray)
            .Expand();

        var statusPanel = new Panel(BuildStatusMarkup(engine, options, stats))
            .Header(" udpping ")
            .RoundedBorder().BorderColor(Color.Gray)
            .Expand();

        return new Rows(statusPanel, graphPanel);
    }

    private static (double Min, double Max) DetermineScale(AggregateStats stats)
    {
        if (stats.Count == 0)
        {
            return (0, 100);
        }

        var min = stats.Min;
        var max = stats.Max;

        if (max - min < 1.0)
        {
            // A flat line still deserves a visible band around it.
            min = Math.Max(0, min - 5);
            max += 5;
        }

        return (min, max);
    }

    private static Cell[,] BuildGrid(TickSnapshot[] snapshots, double scaleMin, double scaleMax)
    {
        var grid = new Cell[GraphHeight, snapshots.Length];
        for (var r = 0; r < GraphHeight; r++)
        {
            for (var c = 0; c < snapshots.Length; c++)
            {
                grid[r, c] = Cell.Empty;
            }
        }

        for (var c = 0; c < snapshots.Length; c++)
        {
            var snap = snapshots[c];

            if (snap.CompletedMs.Length > 0)
            {
                // Column stats need sorted data for the median; sort a
                // local copy so the engine's stored array is untouched.
                var sorted = (double[])snap.CompletedMs.Clone();
                Array.Sort(sorted);

                var min = sorted[0];
                var max = sorted[^1];
                var median = Median(sorted);

                var rowMedian = MapRow(median, scaleMin, scaleMax);
                var rowMax = MapRow(max, scaleMin, scaleMax);
                var rowMin = MapRow(min, scaleMin, scaleMax);

                var background = snap.TimedOut > 0 ? "red" : snap.Pending > 0 ? "green" : null;

                // Order matters: median is written first so that a max or
                // min landing on the same row correctly collapses to '-'
                // via Cell.Combine, and the background (which only ever
                // comes from the max write) survives that collapse.
                grid[rowMedian, c] = Cell.Combine(grid[rowMedian, c], '╋', null, "blue");
                grid[rowMax, c] = Cell.Combine(grid[rowMax, c], '┬', background, "darkorange");
                grid[rowMin, c] = Cell.Combine(grid[rowMin, c], '┴', null, "darkgreen");
                for (var r = rowMin - 1; r > rowMedian; r--) grid[r, c] = Cell.Combine(grid[r, c], '│', null, "darkgreen");
                for (var r = rowMedian - 1; r > rowMax; r--) grid[r, c] = Cell.Combine(grid[r, c], '│', null, "darkorange");
            }
            else if (snap.TimedOut > 0 || snap.Pending > 0)
            {
                // No completed samples yet this tick, but something is
                // known about it - show a colored placeholder so the
                // column isn't silently blank.
                var background = snap.TimedOut > 0 ? "red" : "green";
                grid[GraphHeight - 1, c] = new Cell(' ', background, null);
            }
        }

        return grid;
    }

    private static string BuildGraphMarkup(Cell[,] grid, double scaleMin, double scaleMax)
    {
        var columns = grid.GetLength(1);
        var sb = new StringBuilder();

        for (var row = 0; row < GraphHeight; row++)
        {
            if (row > 0)
            {
                sb.Append('\n');
            }

            var label = row switch
            {
                0 => $"{scaleMax:0}",
                GraphHeight - 1 => $"{scaleMin:0}",
                _ => string.Empty,
            };

            sb.Append(label.PadLeft(LabelWidth));
            sb.Append(' ');

            for (var c = 0; c < columns; c++)
            {
                grid[row, c].AppendMarkup(sb);
            }
        }

        return sb.ToString();
    }

    private static double Median(double[] sorted)
    {
        var n = sorted.Length;
        return n % 2 == 1
            ? sorted[n / 2]
            : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    private static int MapRow(double value, double scaleMin, double scaleMax)
    {
        var t = (value - scaleMin) / (scaleMax - scaleMin);
        t = Math.Clamp(t, 0.0, 1.0);
        var row = (int)Math.Round((1.0 - t) * (GraphHeight - 1));
        return Math.Clamp(row, 0, GraphHeight - 1);
    }

    private static Markup BuildStatusMarkup(PingEngine engine, CliOptions options, AggregateStats stats)
    {
        static string Fmt(double v) => double.IsNaN(v) ? "--" : $"{v:0}ms";

        var line1 =
            $"[bold]Interval:[/] {options.IntervalMs}ms    " +
            $"[bold]Timeout:[/] {options.TimeoutMs}ms    " +
            $"[bold]Display:[/] {options.DisplayIntervalMs}ms";

        var line2 =
            $"[bold]Min:[/] {Fmt(stats.Min)}   [bold]Max:[/] {Fmt(stats.Max)}   " +
            $"[bold]Median:[/] {Fmt(stats.Median)}   [bold]Last:[/] {Fmt(engine.LastRttMs)}   " +
            $"[bold]Sent:[/] {engine.SentCount}   [bold]Lost:[/] {engine.LostCount}";

        return new Markup(line1 + "\n" + line2);
    }
}
