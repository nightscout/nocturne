using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Nocturne.Widget.Windows11;

/// <summary>
/// Renders a small circular-arrow "refresh" glyph as a transparent PNG data URI, so the Adaptive
/// Card can show refresh as a tappable corner icon instead of a full-width button. Drawn once and
/// cached. Slate-coloured to read on both light and dark widget backgrounds.
/// </summary>
internal static class RefreshIcon
{
    private static string? _cached;

    /// <summary>Gets the cached <c>data:image/png;base64,…</c> URI for the refresh glyph.</summary>
    public static string DataUri => _cached ??= Render();

    private static string Render()
    {
        const int size = 32;
        var color = Color.FromArgb(235, 148, 163, 184); // slate, matches the sparkline line

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        const float margin = 6f;
        var rect = new RectangleF(margin, margin, size - (2 * margin), size - (2 * margin));
        var cx = size / 2f;
        var cy = size / 2f;
        var r = rect.Width / 2f;

        // Open arc with a gap; the gap end carries the arrowhead.
        const float startAngle = 40f;
        const float sweep = 280f;
        using (var pen = new Pen(color, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawArc(pen, rect, startAngle, sweep);
        }

        // Arrowhead at the swept end, oriented along the tangent (clockwise sweep => tangent +90°).
        var endDeg = startAngle + sweep;
        var endRad = endDeg * Math.PI / 180.0;
        var ex = (float)(cx + (r * Math.Cos(endRad)));
        var ey = (float)(cy + (r * Math.Sin(endRad)));
        var tan = (endDeg + 90f) * Math.PI / 180.0;

        PointF AlongTangent(float forward, float side) => new(
            ex + (float)((forward * Math.Cos(tan)) - (side * Math.Sin(tan))),
            ey + (float)((forward * Math.Sin(tan)) + (side * Math.Cos(tan)))
        );

        const float h = 5.5f;
        var head = new[] { AlongTangent(h, 0f), AlongTangent(-h, h), AlongTangent(-h, -h) };
        using (var brush = new SolidBrush(color))
        {
            g.FillPolygon(brush, head);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }
}
