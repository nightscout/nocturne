using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Nocturne.Widget.Windows11;

/// <summary>
/// Renders a horizontal time-in-range bar (low / in-range / high) as a transparent PNG data URI.
/// Segment widths are proportional to the supplied percentages. Colours match the sparkline so the
/// widgets read as a set.
/// </summary>
internal static class TirBar
{
    private static readonly Color Low = Color.FromArgb(255, 239, 68, 68);     // red
    private static readonly Color InRange = Color.FromArgb(255, 34, 197, 94); // green
    private static readonly Color High = Color.FromArgb(255, 245, 158, 11);   // amber

    /// <summary>
    /// Renders the bar. Percentages need not sum to exactly 100 (rounding); they are normalised.
    /// Horizontal runs low → in-range → high (left to right); vertical runs high → in-range → low
    /// (top to bottom, AGP convention).
    /// </summary>
    public static string RenderDataUri(
        int lowPct, int inRangePct, int highPct, int width, int height, bool vertical = false)
    {
        var total = lowPct + inRangePct + highPct;
        if (total <= 0)
        {
            total = 100; // nothing to draw; emit a transparent bar rather than divide by zero
        }

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        var segments = vertical
            ? new[] { (highPct, High), (inRangePct, InRange), (lowPct, Low) }
            : new[] { (lowPct, Low), (inRangePct, InRange), (highPct, High) };

        var pos = 0f;
        foreach (var (pct, color) in segments)
        {
            if (pct <= 0)
            {
                continue;
            }

            var extent = (float)pct / total * (vertical ? height : width);
            using var brush = new SolidBrush(color);
            if (vertical)
            {
                g.FillRectangle(brush, 0, pos, width, extent + 0.5f); // +0.5 closes anti-alias seams
            }
            else
            {
                g.FillRectangle(brush, pos, 0, extent + 0.5f, height);
            }

            pos += extent;
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }
}
