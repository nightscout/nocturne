using Nocturne.Core.Constants;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Reads the unit spellings Nightscout and its uploaders send, and converts between the units.
/// </summary>
public static class GlucoseUnits
{
    /// <summary>
    /// Reads a units string by its letters alone, ignoring case, so "mmol/L", "MMOL" and "mmol l" are
    /// <see cref="GlucoseUnit.Mmol"/> and "mg/dL", "mgdl" and "mg" are <see cref="GlucoseUnit.MgDl"/>.
    /// Null for anything else: each caller states the unit it assumes then.
    /// </summary>
    public static GlucoseUnit? Parse(string? units)
    {
        if (string.IsNullOrWhiteSpace(units))
            return null;

        var letters = new string(units.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        return letters switch
        {
            "mmol" or "mmoll" => GlucoseUnit.Mmol,
            "mgdl" or "mg" => GlucoseUnit.MgDl,
            _ => null,
        };
    }

    /// <summary>A value read in <paramref name="unit"/>, in mg/dL. Not rounded.</summary>
    public static double ToMgdl(double value, GlucoseUnit unit) =>
        unit == GlucoseUnit.Mmol ? value * GlucoseConstants.MgdlPerMmol : value;

    /// <summary>
    /// A mg/dL value for display in <paramref name="unit"/>: whole mg/dL, or mmol/L to one decimal.
    /// </summary>
    public static double FromMgdl(double mgdl, GlucoseUnit unit) =>
        unit == GlucoseUnit.Mmol
            ? ScaledRounding.Round(mgdl / GlucoseConstants.MgdlPerMmol, 1)
            : Math.Round(mgdl);
}
