using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Nocturne.Desktop.Tray.Helpers;
using Nocturne.Core.Models.Widget;
using Nocturne.Desktop.Tray.Extensions;
using Nocturne.Desktop.Tray.Models;
using Windows.Storage.Streams;

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

    public async Task<byte[]> RenderIconAsync(V4GlucoseReading? reading, TraySettings settings)
    {
        using var renderTarget = new CanvasRenderTarget(_device, IconSize, IconSize, Dpi);

        using (var session = renderTarget.CreateDrawingSession())
        {
            session.Clear(Colors.Transparent);

            if (reading is null || TimeAgoHelper.IsStale(reading.GetTimestamp(), staleMinutes: 15))
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

    private void DrawGlucoseIcon(CanvasDrawingSession session, V4GlucoseReading reading, TraySettings settings)
    {
        var color = GlucoseRangeHelper.GetColor(
            reading.Sgv,
            settings.UrgentLowThreshold,
            settings.LowThreshold,
            settings.HighThreshold,
            settings.UrgentHighThreshold);

        var displayValue = GlucoseRangeHelper.FormatValue(reading.Sgv, settings.Unit);

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
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };

        session.DrawText(
            displayValue,
            new System.Numerics.Vector2(IconSize / 2f, IconSize / 2f),
            color,
            textFormat);
    }

    private static void DrawStaleIcon(CanvasDrawingSession session, V4GlucoseReading? reading)
    {
        var color = GlucoseRangeHelper.StaleColor;

        using var textFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = 14f,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
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
