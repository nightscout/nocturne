using Microsoft.UI;
using Nocturne.Widget.Contracts;
using Nocturne.Widget.Contracts.Helpers;
using Windows.UI;

namespace Nocturne.Desktop.Tray.Helpers;

/// <summary>
/// Maps glucose values to colors based on configured thresholds.
/// Pure formatting and unit conversion are delegated to <see cref="GlucoseFormatHelper"/>.
/// </summary>
public static class GlucoseRangeHelper
{
    /// <summary>
    /// Standard conversion factor from mg/dL to mmol/L.
    /// Provided here for backward compatibility; prefer <see cref="GlucoseFormatHelper.MgdlToMmolFactor"/>.
    /// </summary>
    public const double MgdlToMmolFactor = GlucoseFormatHelper.MgdlToMmolFactor;

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

    /// <summary>
    /// Formats a glucose value for display. Delegates to <see cref="GlucoseFormatHelper.FormatValue"/>.
    /// </summary>
    public static string FormatValue(double mgdl, GlucoseUnit unit) =>
        GlucoseFormatHelper.FormatValue(mgdl, unit);

    /// <summary>
    /// Formats a glucose delta for display. Delegates to <see cref="GlucoseFormatHelper.FormatDelta"/>.
    /// </summary>
    public static string FormatDelta(double? delta, GlucoseUnit unit) =>
        GlucoseFormatHelper.FormatDelta(delta, unit);
}
