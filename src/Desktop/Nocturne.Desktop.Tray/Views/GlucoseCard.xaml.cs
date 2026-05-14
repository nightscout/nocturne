using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nocturne.Desktop.Tray.Helpers;
using Nocturne.Core.Models.Widget;
using Nocturne.Desktop.Tray.Extensions;
using Nocturne.Desktop.Tray.Models;
using Nocturne.Widget.Contracts;
using GlucoseUnit = Nocturne.Widget.Contracts.GlucoseUnit;
using Windows.UI;

namespace Nocturne.Desktop.Tray.Views;

public sealed partial class GlucoseCard : UserControl
{
    public GlucoseCard()
    {
        this.InitializeComponent();
    }

    public void Update(V4GlucoseReading? reading, TraySettings settings)
    {
        if (reading is null)
        {
            BgValueText.Text = "---";
            BgValueText.Foreground = new SolidColorBrush(GlucoseRangeHelper.StaleColor);
            TrendArrowText.Text = "";
            DeltaText.Text = "";
            TimeAgoText.Text = "No data";
            RangeLabelText.Text = "";
            UnitText.Text = settings.Unit == GlucoseUnit.MmolL ? "mmol/L" : "mg/dL";
            return;
        }

        var color = GlucoseRangeHelper.GetColor(
            reading.Sgv,
            settings.UrgentLowThreshold,
            settings.LowThreshold,
            settings.HighThreshold,
            settings.UrgentHighThreshold);

        var brush = new SolidColorBrush(color);

        BgValueText.Text = GlucoseRangeHelper.FormatValue(reading.Sgv, settings.Unit);
        BgValueText.Foreground = brush;

        TrendArrowText.Text = "\uE74A";
        TrendArrowText.Foreground = brush;
        TrendArrowRotation.Angle = TrendHelper.GetArrowRotation(reading.Direction.ToString());

        var delta = GlucoseRangeHelper.FormatDelta(reading.Delta, settings.Unit);
        DeltaText.Text = delta;
        UnitText.Text = settings.Unit == GlucoseUnit.MmolL ? "mmol/L" : "mg/dL";

        TimeAgoText.Text = TimeAgoHelper.Format(reading.GetTimestamp());

        var isStale = TimeAgoHelper.IsStale(reading.GetTimestamp());
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
            reading.Sgv,
            settings.UrgentLowThreshold,
            settings.LowThreshold,
            settings.HighThreshold,
            settings.UrgentHighThreshold);
        RangeLabelText.Foreground = brush;
    }
}
