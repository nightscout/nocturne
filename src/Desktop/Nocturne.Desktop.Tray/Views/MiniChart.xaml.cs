using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Nocturne.Desktop.Tray.Helpers;
using Nocturne.Desktop.Tray.Models;
using Windows.Foundation;
using Windows.UI;

namespace Nocturne.Desktop.Tray.Views;

/// <summary>
/// A lightweight sparkline chart showing recent glucose readings.
/// Draws directly onto a Canvas with range bands and a time axis.
/// </summary>
public sealed partial class MiniChart : UserControl
{
    private const double MinMgdl = 40;
    private const double MaxMgdl = 300;
    private const double DotRadius = 3;
    private const double XAxisHeight = 20;

    public MiniChart()
    {
        this.InitializeComponent();
        this.SizeChanged += OnSizeChanged;
    }

    private IReadOnlyList<GlucoseReading>? _readings;
    private TraySettings? _settings;

    public void Update(IReadOnlyList<GlucoseReading> readings, TraySettings settings)
    {
        _readings = readings;
        _settings = settings;
        Render();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Render();
    }

    private void Render()
    {
        ChartCanvas.Children.Clear();

        if (_settings is null)
            return;

        var width = ChartCanvas.ActualWidth;
        var canvasHeight = ChartCanvas.ActualHeight;
        if (width <= 0 || canvasHeight <= 0) return;

        var chartHeight = canvasHeight - XAxisHeight;
        if (chartHeight <= 0) return;

        // Clip canvas to prevent children bleeding outside bounds
        ChartCanvas.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, width, canvasHeight),
        };

        // Time window based on configured hours, not data range
        var now = DateTimeOffset.UtcNow;
        var windowEndMills = now.ToUnixTimeMilliseconds();
        var windowStartMills = now.AddHours(-_settings.ChartHours).ToUnixTimeMilliseconds();

        DrawRangeBands(width, chartHeight);
        DrawXAxis(width, chartHeight, windowStartMills, windowEndMills);

        if (_readings is not null && _readings.Count >= 2)
        {
            DrawReadings(width, chartHeight, windowStartMills, windowEndMills);
        }

        HighLabel.Text = GlucoseRangeHelper.FormatValue(_settings.HighThreshold, _settings.Unit);
        LowLabel.Text = GlucoseRangeHelper.FormatValue(_settings.LowThreshold, _settings.Unit);
    }

    private void DrawRangeBands(double width, double chartHeight)
    {
        if (_settings is null) return;

        var lowY = MgdlToY(_settings.LowThreshold, chartHeight);
        var highY = MgdlToY(_settings.HighThreshold, chartHeight);

        var rangeBand = new Rectangle
        {
            Width = width,
            Height = Math.Max(0, lowY - highY),
            Fill = new SolidColorBrush(Color.FromArgb(20, 60, 180, 75)),
        };
        Canvas.SetLeft(rangeBand, 0);
        Canvas.SetTop(rangeBand, highY);
        ChartCanvas.Children.Add(rangeBand);

        var highLine = CreateDashedLine(0, highY, width, highY, Color.FromArgb(40, 230, 160, 30));
        ChartCanvas.Children.Add(highLine);

        var lowLine = CreateDashedLine(0, lowY, width, lowY, Color.FromArgb(40, 230, 160, 30));
        ChartCanvas.Children.Add(lowLine);
    }

    private void DrawXAxis(double width, double chartHeight, long windowStartMills, long windowEndMills)
    {
        var millsRange = windowEndMills - windowStartMills;
        if (millsRange <= 0) return;

        // Baseline
        ChartCanvas.Children.Add(new Line
        {
            X1 = 0, Y1 = chartHeight,
            X2 = width, Y2 = chartHeight,
            Stroke = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)),
            StrokeThickness = 1,
        });

        // Determine tick interval based on chart hours
        var tickIntervalHours = _settings!.ChartHours > 6 ? 2 : 1;

        // Find the first whole hour after windowStart, aligned to tick interval
        var windowStart = DateTimeOffset.FromUnixTimeMilliseconds(windowStartMills).ToLocalTime();
        var firstHour = new DateTimeOffset(
            windowStart.Year, windowStart.Month, windowStart.Day,
            windowStart.Hour, 0, 0, windowStart.Offset).AddHours(1);

        if (tickIntervalHours > 1)
        {
            while (firstHour.Hour % tickIntervalHours != 0)
                firstHour = firstHour.AddHours(1);
        }

        var windowEnd = DateTimeOffset.FromUnixTimeMilliseconds(windowEndMills).ToLocalTime();

        for (var tick = firstHour; tick < windowEnd; tick = tick.AddHours(tickIntervalHours))
        {
            var tickMills = tick.ToUnixTimeMilliseconds();
            var x = ((tickMills - windowStartMills) / (double)millsRange) * width;

            // Skip ticks too close to edges where labels would clip
            if (x < 20 || x > width - 20) continue;

            // Vertical gridline
            ChartCanvas.Children.Add(new Line
            {
                X1 = x, Y1 = 0,
                X2 = x, Y2 = chartHeight,
                Stroke = new SolidColorBrush(Color.FromArgb(15, 128, 128, 128)),
                StrokeThickness = 1,
            });

            // Small tick mark below baseline
            ChartCanvas.Children.Add(new Line
            {
                X1 = x, Y1 = chartHeight,
                X2 = x, Y2 = chartHeight + 4,
                Stroke = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
                StrokeThickness = 1,
            });

            // Time label
            var label = new TextBlock
            {
                Text = tick.ToString("H:mm"),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(80, 200, 200, 200)),
            };
            Canvas.SetLeft(label, x - 14);
            Canvas.SetTop(label, chartHeight + 4);
            ChartCanvas.Children.Add(label);
        }
    }

    private void DrawReadings(double width, double chartHeight, long windowStartMills, long windowEndMills)
    {
        if (_readings is null || _settings is null) return;

        var millsRange = windowEndMills - windowStartMills;
        if (millsRange <= 0) return;

        // Break polyline into segments at data gaps (> 15 minutes between readings)
        const long maxGapMills = 15 * 60 * 1000;
        var segment = new Polyline
        {
            StrokeThickness = 1.5,
            Stroke = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
        };
        long lastMills = 0;

        foreach (var reading in _readings)
        {
            if (lastMills > 0 && reading.Mills - lastMills > maxGapMills && segment.Points.Count > 0)
            {
                if (segment.Points.Count >= 2)
                    ChartCanvas.Children.Add(segment);

                segment = new Polyline
                {
                    StrokeThickness = 1.5,
                    Stroke = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
                };
            }

            var x = ((reading.Mills - windowStartMills) / (double)millsRange) * width;
            var y = MgdlToY(reading.Mgdl, chartHeight);
            segment.Points.Add(new Point(x, y));
            lastMills = reading.Mills;
        }

        if (segment.Points.Count >= 2)
            ChartCanvas.Children.Add(segment);

        // Draw dots
        foreach (var reading in _readings)
        {
            var x = ((reading.Mills - windowStartMills) / (double)millsRange) * width;
            var y = MgdlToY(reading.Mgdl, chartHeight);

            var color = GlucoseRangeHelper.GetColor(
                reading.Mgdl,
                _settings.UrgentLowThreshold,
                _settings.LowThreshold,
                _settings.HighThreshold,
                _settings.UrgentHighThreshold);

            var dot = new Ellipse
            {
                Width = DotRadius * 2,
                Height = DotRadius * 2,
                Fill = new SolidColorBrush(color),
            };

            Canvas.SetLeft(dot, x - DotRadius);
            Canvas.SetTop(dot, y - DotRadius);
            ChartCanvas.Children.Add(dot);
        }
    }

    private static double MgdlToY(double mgdl, double chartHeight)
    {
        var clamped = Math.Clamp(mgdl, MinMgdl, MaxMgdl);
        var normalized = (clamped - MinMgdl) / (MaxMgdl - MinMgdl);
        return chartHeight - (normalized * chartHeight);
    }

    private static Line CreateDashedLine(double x1, double y1, double x2, double y2, Color color)
    {
        return new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1,
            StrokeDashArray = [4, 4],
        };
    }
}
