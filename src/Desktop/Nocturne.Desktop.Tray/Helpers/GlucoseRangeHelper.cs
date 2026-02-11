using Microsoft.UI;
using Windows.UI;

namespace Nocturne.Desktop.Tray.Helpers;

/// <summary>
/// Maps glucose values to colors based on configured thresholds,
/// and provides formatting utilities for glucose values.
/// </summary>
public static class GlucoseRangeHelper
{
    /// <summary>
    /// Standard conversion factor from mg/dL to mmol/L (molecular weight of glucose / 10).
    /// </summary>
    public const double MgdlToMmolFactor = 18.01559;

    public static readonly Color UrgentColor = Color.FromArgb(255, 200, 30, 30);
    public static readonly Color WarningColor = Color.FromArgb(255, 230, 160, 30);
    public static readonly Color InRangeColor = Color.FromArgb(255, 60, 180, 75);
    public static readonly Color StaleColor = Color.FromArgb(255, 128, 128, 128);

    public static Color GetColor(double mgdl, int urgentLow, int low, int high, int urgentHigh)
    {
        return mgdl switch
        {
            _ when mgdl <= urgentLow => UrgentColor,
            _ when mgdl <= low => WarningColor,
            _ when mgdl >= urgentHigh => UrgentColor,
            _ when mgdl >= high => WarningColor,
            _ => InRangeColor,
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
            Models.GlucoseUnit.MmolL => (mgdl / MgdlToMmolFactor).ToString("F1"),
            _ => ((int)mgdl).ToString(),
        };
    }

    public static string FormatDelta(double? delta, Models.GlucoseUnit unit)
    {
        if (delta is null) return "";
        var value = unit == Models.GlucoseUnit.MmolL ? delta.Value / MgdlToMmolFactor : delta.Value;
        var formatted = unit == Models.GlucoseUnit.MmolL ? value.ToString("+0.0;-0.0;0.0") : value.ToString("+0;-0;0");
        return formatted;
    }
}
