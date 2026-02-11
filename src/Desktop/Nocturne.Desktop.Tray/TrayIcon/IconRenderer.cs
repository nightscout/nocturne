using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Nocturne.Desktop.Tray.Helpers;
using Nocturne.Desktop.Tray.Models;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace Nocturne.Desktop.Tray.TrayIcon;

/// <summary>
/// Renders the current glucose value and trend into a 32x32 icon bitmap for the system tray.
/// Uses Win2D (Microsoft.Graphics.Canvas) for high-quality text rendering.
/// </summary>
public sealed class IconRenderer : IDisposable
{
    private const int IconSize = 32;
    private const float Dpi = 96f;

    private readonly CanvasDevice _device;
    private bool _disposed;

    public IconRenderer()
    {
        _device = CanvasDevice.GetSharedDevice();
    }

    /// <summary>
    /// Renders a glucose reading into a tray icon bitmap.
    /// The icon shows the BG value in the center, color-coded by range.
    /// </summary>
    public async Task<byte[]> RenderIconAsync(GlucoseReading? reading, TraySettings settings)
    {
        using var renderTarget = new CanvasRenderTarget(_device, IconSize, IconSize, Dpi);

        using (var session = renderTarget.CreateDrawingSession())
        {
            session.Clear(Colors.Transparent);

            if (reading is null || TimeAgoHelper.IsStale(reading.Timestamp, staleMinutes: 15))
            {
                DrawStaleIcon(session, reading);
            }
            else
            {
                DrawGlucoseIcon(session, reading, settings);
            }
        }

        return await ConvertToPngBytesAsync(renderTarget);
    }

    private void DrawGlucoseIcon(CanvasDrawingSession session, GlucoseReading reading, TraySettings settings)
    {
        var color = GlucoseRangeHelper.GetColor(
            reading.Mgdl,
            settings.UrgentLowThreshold,
            settings.LowThreshold,
            settings.HighThreshold,
            settings.UrgentHighThreshold);

        var displayValue = GlucoseRangeHelper.FormatValue(reading.Mgdl, settings.Unit);

        // Determine font size based on the length of the display value
        var fontSize = displayValue.Length switch
        {
            1 => 22f,
            2 => 18f,
            3 => 14f,
            _ => 11f,
        };

        using var textFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = fontSize,
            FontWeight = Windows.UI.Text.FontWeights.Bold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };

        // Draw text centered in the icon
        session.DrawText(
            displayValue,
            new System.Numerics.Vector2(IconSize / 2f, IconSize / 2f),
            color,
            textFormat);
    }

    private static void DrawStaleIcon(CanvasDrawingSession session, GlucoseReading? reading)
    {
        // Stale or no data: draw "---" in gray
        var color = Color.FromArgb(255, 128, 128, 128);

        using var textFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = 14f,
            FontWeight = Windows.UI.Text.FontWeights.Bold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };

        var text = reading is null ? "---" : "old";
        session.DrawText(
            text,
            new System.Numerics.Vector2(IconSize / 2f, IconSize / 2f),
            color,
            textFormat);
    }

    private static async Task<byte[]> ConvertToPngBytesAsync(CanvasRenderTarget renderTarget)
    {
        using var stream = new InMemoryRandomAccessStream();
        await renderTarget.SaveAsync(stream, CanvasBitmapFileFormat.Png);

        stream.Seek(0);
        var bytes = new byte[stream.Size];
        var buffer = await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
        return buffer.ToArray();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _device.Dispose();
            _disposed = true;
        }
    }
}
