using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Nocturne.Core.Models.Widget;

namespace Nocturne.Widget.Windows11;

/// <summary>
/// Renders a compact glucose trend sparkline (recent history plus optional predictions) as a
/// transparent PNG data URI for embedding in an Adaptive Card Image element. Drawn in mg/dL
/// space, so the shape is identical regardless of the user's display unit. Colours are chosen
/// to read on both light and dark widget backgrounds.
/// </summary>
internal static class GlucoseSparkline
{
    private const double TargetLow = 70;
    private const double TargetHigh = 180;

    private static readonly Color InRange = Color.FromArgb(255, 34, 197, 94);     // green
    private static readonly Color Low = Color.FromArgb(255, 239, 68, 68);         // red
    private static readonly Color High = Color.FromArgb(255, 245, 158, 11);       // amber
    private static readonly Color Band = Color.FromArgb(46, 34, 197, 94);         // green, ~18% alpha
    private static readonly Color Line = Color.FromArgb(200, 148, 163, 184);      // slate
    private static readonly Color Predicted = Color.FromArgb(170, 148, 163, 184); // slate, dashed

    /// <summary>
    /// Renders the sparkline. Returns a <c>data:image/png;base64,…</c> URI, or <c>null</c> when
    /// there are too few points to draw a meaningful line.
    /// </summary>
    public static string? RenderDataUri(V4SummaryResponse summary, int width, int height)
    {
        var points = summary.History.Select(h => (h.Mills, h.Sgv)).ToList();
        if (summary.Current is not null)
        {
            points.Add((summary.Current.Mills, summary.Current.Sgv));
        }

        points = points.Where(p => p.Sgv > 0).OrderBy(p => p.Mills).ToList();
        if (points.Count < 2)
        {
            return null;
        }

        var preds = new List<(long Mills, double Sgv)>();
        var p = summary.Predictions;
        if (p?.Values is { Count: > 0 } && p.IntervalMills > 0)
        {
            for (var i = 0; i < p.Values.Count; i++)
            {
                preds.Add((p.StartMills + (i * p.IntervalMills), p.Values[i]));
            }
        }

        var minMills = points[0].Mills;
        var maxMills = preds.Count > 0 ? preds[^1].Mills : points[^1].Mills;
        if (maxMills <= minMills)
        {
            maxMills = minMills + 1;
        }

        var allVals = points.Select(pt => pt.Sgv).Concat(preds.Select(pt => pt.Sgv)).ToList();
        var yMin = Math.Min(allVals.Min(), TargetLow) - 10;
        var yMax = Math.Max(allVals.Max(), TargetHigh) + 10;
        if (yMax <= yMin)
        {
            yMax = yMin + 1;
        }

        const int padL = 2, padR = 2, padT = 4, padB = 4;
        var plotW = width - padL - padR;
        var plotH = height - padT - padB;

        float X(long m) => padL + (float)((m - minMills) / (double)(maxMills - minMills) * plotW);
        float Y(double v) => padT + (float)((yMax - v) / (yMax - yMin) * plotH);

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Target-range band
        using (var bandBrush = new SolidBrush(Band))
        {
            var yHigh = Y(TargetHigh);
            var yLow = Y(TargetLow);
            g.FillRectangle(bandBrush, padL, yHigh, plotW, Math.Max(1f, yLow - yHigh));
        }

        // History line
        using (var linePen = new Pen(Line, 2f))
        {
            var path = points.Select(pt => new PointF(X(pt.Mills), Y(pt.Sgv))).ToArray();
            g.DrawLines(linePen, path);
        }

        // Predictions, dashed, continuing from the current reading
        if (preds.Count > 0 && summary.Current is not null)
        {
            using var predPen = new Pen(Predicted, 2f) { DashStyle = DashStyle.Dash };
            var seq = new List<PointF> { new(X(summary.Current.Mills), Y(summary.Current.Sgv)) };
            seq.AddRange(preds.Select(pt => new PointF(X(pt.Mills), Y(pt.Sgv))));
            g.DrawLines(predPen, seq.ToArray());
        }

        // Reading dots, coloured by range
        foreach (var pt in points)
        {
            using var b = new SolidBrush(ColorFor(pt.Sgv));
            g.FillEllipse(b, X(pt.Mills) - 2.2f, Y(pt.Sgv) - 2.2f, 4.4f, 4.4f);
        }

        // Emphasise the current reading
        if (summary.Current is not null)
        {
            var x = X(summary.Current.Mills);
            var y = Y(summary.Current.Sgv);
            using var b = new SolidBrush(ColorFor(summary.Current.Sgv));
            using var ring = new Pen(Color.FromArgb(220, 255, 255, 255), 1.5f);
            g.FillEllipse(b, x - 3.5f, y - 3.5f, 7f, 7f);
            g.DrawEllipse(ring, x - 3.5f, y - 3.5f, 7f, 7f);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }

    private static Color ColorFor(double sgv) =>
        sgv < TargetLow ? Low : sgv > TargetHigh ? High : InRange;
}
