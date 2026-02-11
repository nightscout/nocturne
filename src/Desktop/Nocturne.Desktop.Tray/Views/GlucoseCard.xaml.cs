using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nocturne.Desktop.Tray.Helpers;
using Nocturne.Desktop.Tray.Models;
using Windows.UI;

namespace Nocturne.Desktop.Tray.Views;

public sealed partial class GlucoseCard : UserControl
{
    public GlucoseCard()
    {
        this.InitializeComponent();
    }

    public void Update(GlucoseReading? reading, TraySettings settings)
    {
        if (reading is null)
        {
            BgValueText.Text = "---";
            BgValueText.Foreground = new SolidColorBrush(Color.FromArgb(255, 128, 128, 128));
            TrendArrowText.Text = "";
            DeltaText.Text = "";
            TimeAgoText.Text = "No data";
            RangeLabelText.Text = "";
            UnitText.Text = settings.Unit == GlucoseUnit.MmolL ? "mmol/L" : "mg/dL";
            return;
        }

        var color = GlucoseRangeHelper.GetColor(
            reading.Mgdl,
            settings.UrgentLowThreshold,
            settings.LowThreshold,
            settings.HighThreshold,
            settings.UrgentHighThreshold);

        var brush = new SolidColorBrush(color);

        BgValueText.Text = GlucoseRangeHelper.FormatValue(reading.Mgdl, settings.Unit);
        BgValueText.Foreground = brush;

        TrendArrowText.Text = "\uE74A"; // ChevronUp glyph, rotated via transform
        TrendArrowText.Foreground = brush;
        TrendArrowRotation.Angle = TrendHelper.GetArrowRotation(reading.Direction);

        var delta = GlucoseRangeHelper.FormatDelta(reading.Delta, settings.Unit);
        DeltaText.Text = delta;
        UnitText.Text = settings.Unit == GlucoseUnit.MmolL ? "mmol/L" : "mg/dL";

        TimeAgoText.Text = TimeAgoHelper.Format(reading.Timestamp);

        var isStale = TimeAgoHelper.IsStale(reading.Timestamp);
        if (isStale)
        {
            BgValueText.Opacity = 0.5;
            TimeAgoText.Text += " (stale)";
        }
        else
        {
            BgValueText.Opacity = 1.0;
        }

        RangeLabelText.Text = GlucoseRangeHelper.GetRangeLabel(
            reading.Mgdl,
            settings.UrgentLowThreshold,
            settings.LowThreshold,
            settings.HighThreshold,
            settings.UrgentHighThreshold);
        RangeLabelText.Foreground = brush;
    }
}
