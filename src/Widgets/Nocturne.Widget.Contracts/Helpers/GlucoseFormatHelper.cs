using Nocturne.Core.Constants;

namespace Nocturne.Widget.Contracts.Helpers;

/// <summary>
/// Platform-independent glucose value formatting and unit conversion utilities.
/// </summary>
public static class GlucoseFormatHelper
{
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
            GlucoseUnit.MmolL => (mgdl / GlucoseConstants.MgdlPerMmol).ToString("F1"),
            _ => ((int)mgdl).ToString(),
        };
    }

    /// <summary>
    /// Converts a glucose value from mg/dL to mmol/L.
    /// </summary>
    public static double ToMmol(double mgdl) => mgdl / GlucoseConstants.MgdlPerMmol;

    /// <summary>
    /// Converts a glucose value from mmol/L to mg/dL.
    /// </summary>
    public static double ToMgdl(double mmol) => mmol * GlucoseConstants.MgdlPerMmol;

    /// <summary>
    /// Formats a glucose delta value with sign prefix for the given unit.
    /// </summary>
    /// <param name="delta">Delta value in mg/dL, or null.</param>
    /// <param name="unit">The display unit.</param>
    /// <returns>Formatted delta string (e.g. "+5", "-0.3"), or empty string if null.</returns>
    public static string FormatDelta(double? delta, GlucoseUnit unit)
    {
        if (delta is null)
            return "";
        var value =
            unit == GlucoseUnit.MmolL ? delta.Value / GlucoseConstants.MgdlPerMmol : delta.Value;
        var formatted =
            unit == GlucoseUnit.MmolL ? value.ToString("+0.0;-0.0;0.0") : value.ToString("+0;-0;0");
        return formatted;
    }
}
