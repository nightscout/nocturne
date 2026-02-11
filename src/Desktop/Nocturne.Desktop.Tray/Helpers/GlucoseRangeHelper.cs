using Microsoft.UI;
using Windows.UI;

namespace Nocturne.Desktop.Tray.Helpers;

/// <summary>
/// Maps glucose values to colors based on configured thresholds.
/// </summary>
public static class GlucoseRangeHelper
{
    public static Color GetColor(double mgdl, int urgentLow, int low, int high, int urgentHigh)
    {
        return mgdl switch
        {
            _ when mgdl <= urgentLow => Color.FromArgb(255, 200, 30, 30),   // Urgent low - red
            _ when mgdl <= low => Color.FromArgb(255, 230, 160, 30),        // Low - amber
            _ when mgdl >= urgentHigh => Color.FromArgb(255, 200, 30, 30),  // Urgent high - red
            _ when mgdl >= high => Color.FromArgb(255, 230, 160, 30),       // High - amber
            _ => Color.FromArgb(255, 60, 180, 75),                          // In range - green
        };
    }

    public static string GetRangeLabel(double mgdl, int urgentLow, int low, int high, int urgentHigh)
    {
        return mgdl switch
        {
            _ when mgdl <= urgentLow => "URGENT LOW",
            _ when mgdl <= low => "LOW",
            _ when mgdl >= urgentHigh => "URGENT HIGH",
            _ when mgdl >= high => "HIGH",
            _ => "In Range",
        };
    }

    public static string FormatValue(double mgdl, Models.GlucoseUnit unit)
    {
        return unit switch
        {
            Models.GlucoseUnit.MmolL => (mgdl / 18.0).ToString("F1"),
            _ => ((int)mgdl).ToString(),
        };
    }

    public static string FormatDelta(double? delta, Models.GlucoseUnit unit)
    {
        if (delta is null) return "";
        var value = unit == Models.GlucoseUnit.MmolL ? delta.Value / 18.0 : delta.Value;
        var formatted = unit == Models.GlucoseUnit.MmolL ? value.ToString("+0.0;-0.0;0.0") : value.ToString("+0;-0;0");
        return formatted;
    }
}
