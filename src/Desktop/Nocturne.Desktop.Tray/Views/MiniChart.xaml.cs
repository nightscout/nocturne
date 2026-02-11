using Microsoft.UI;
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
/// Draws directly onto a Canvas with range bands.
/// </summary>
public sealed partial class MiniChart : UserControl
{
    private const double MinMgdl = 40;
    private const double MaxMgdl = 300;
    private const double DotRadius = 3;

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

        if (_readings is null || _readings.Count < 2 || _settings is null)
            return;

        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.ActualHeight;
        if (width <= 0 || height <= 0) return;

        // Draw range bands
        DrawRangeBands(width, height);

        // Draw dots for each reading
        DrawReadings(width, height);

        // Update labels
        HighLabel.Text = GlucoseRangeHelper.FormatValue(_settings.HighThreshold, _settings.Unit);
        LowLabel.Text = GlucoseRangeHelper.FormatValue(_settings.LowThreshold, _settings.Unit);
    }

    private void DrawRangeBands(double width, double height)
    {
        if (_settings is null) return;

        var lowY = MgdlToY(_settings.LowThreshold, height);
        var highY = MgdlToY(_settings.HighThreshold, height);

        // In-range band (green, very subtle)
        var rangeBand = new Rectangle
        {
            Width = width,
            Height = Math.Max(0, lowY - highY),
            Fill = new SolidColorBrush(Color.FromArgb(20, 60, 180, 75)),
        };
        Canvas.SetLeft(rangeBand, 0);
        Canvas.SetTop(rangeBand, highY);
        ChartCanvas.Children.Add(rangeBand);

        // High threshold line
        var highLine = CreateDashedLine(0, highY, width, highY, Color.FromArgb(40, 230, 160, 30));
        ChartCanvas.Children.Add(highLine);

        // Low threshold line
        var lowLine = CreateDashedLine(0, lowY, width, lowY, Color.FromArgb(40, 230, 160, 30));
        ChartCanvas.Children.Add(lowLine);
    }

    private void DrawReadings(double width, double height)
    {
        if (_readings is null || _settings is null) return;

        var minMills = _readings[0].Mills;
        var maxMills = _readings[^1].Mills;
        var millsRange = maxMills - minMills;
        if (millsRange <= 0) return;

        // Draw connecting line
        var polyline = new Polyline
        {
            StrokeThickness = 1.5,
            Stroke = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
        };

        foreach (var reading in _readings)
        {
            var x = ((reading.Mills - minMills) / (double)millsRange) * width;
            var y = MgdlToY(reading.Mgdl, height);
            polyline.Points.Add(new Point(x, y));
        }
        ChartCanvas.Children.Add(polyline);

        // Draw dots
        foreach (var reading in _readings)
        {
            var x = ((reading.Mills - minMills) / (double)millsRange) * width;
            var y = MgdlToY(reading.Mgdl, height);

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

    private static double MgdlToY(double mgdl, double height)
    {
        var clamped = Math.Clamp(mgdl, MinMgdl, MaxMgdl);
        var normalized = (clamped - MinMgdl) / (MaxMgdl - MinMgdl);
        return height - (normalized * height); // Invert: high values at top
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
