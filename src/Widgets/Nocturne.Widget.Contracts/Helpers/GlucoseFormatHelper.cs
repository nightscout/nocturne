namespace Nocturne.Widget.Contracts.Helpers;

/// <summary>
/// Platform-independent glucose value formatting and unit conversion utilities.
/// </summary>
public static class GlucoseFormatHelper
{
    /// <summary>
    /// Standard conversion factor from mg/dL to mmol/L (molecular weight of glucose / 10).
    /// </summary>
    public const double MgdlToMmolFactor = 18.01559;

    /// <summary>
    /// Formats a glucose value in mg/dL to the appropriate display string for the given unit.
    /// </summary>
    /// <param name="mgdl">Glucose value in mg/dL.</param>
    /// <param name="unit">The display unit.</param>
    /// <returns>Formatted glucose string (e.g. "120" for mg/dL or "6.7" for mmol/L).</returns>
    public static string FormatValue(double mgdl, GlucoseUnit unit)
    {
        return unit switch
        {
            GlucoseUnit.MmolL => (mgdl / MgdlToMmolFactor).ToString("F1"),
            _ => ((int)mgdl).ToString(),
        };
    }

    /// <summary>
    /// Formats a glucose delta value with sign prefix for the given unit.
    /// </summary>
    /// <param name="delta">Delta value in mg/dL, or null.</param>
    /// <param name="unit">The display unit.</param>
    /// <returns>Formatted delta string (e.g. "+5", "-0.3"), or empty string if null.</returns>
    public static string FormatDelta(double? delta, GlucoseUnit unit)
    {
        if (delta is null) return "";
        var value = unit == GlucoseUnit.MmolL ? delta.Value / MgdlToMmolFactor : delta.Value;
        var formatted = unit == GlucoseUnit.MmolL
            ? value.ToString("+0.0;-0.0;0.0")
            : value.ToString("+0;-0;0");
        return formatted;
    }
}
