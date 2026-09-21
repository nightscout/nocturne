using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Glooko.Xt;

/// <summary>
///     Turns a Glooko XT glucose number into mg/dL. The wire value carries no unit: it is whatever
///     the patient's account displays, so the tenant either states the unit or lets a batch reveal
///     it. Wrong either way misreads every reading by a factor of eighteen, which is why the
///     inference refuses to guess from too little.
/// </summary>
public static class GlookoXtGlucoseUnits
{
    /// <summary>
    ///     No mmol/L reading exceeds this — 35 mmol/L is 630 mg/dL, beyond what any meter or sensor
    ///     reports — while a mg/dL batch of any length passes it on its first normal reading.
    /// </summary>
    private const double MmolCeiling = 35.0;

    public enum Unit
    {
        MgDl,
        Mmol,
    }

    /// <summary>
    ///     The unit <paramref name="setting"/> names, or the one <paramref name="values"/> reveal when
    ///     it says <c>Auto</c>: a batch with any value above the mmol/L ceiling is mg/dL, a batch
    ///     entirely below it is mmol/L, an empty batch has nothing to convert and is called mg/dL.
    /// </summary>
    public static Unit Resolve(string? setting, IEnumerable<double> values)
    {
        switch (setting?.Trim().ToLowerInvariant())
        {
            case "mgdl":
            case "mg/dl":
                return Unit.MgDl;
            case "mmol":
            case "mmol/l":
                return Unit.Mmol;
        }

        var sawAny = false;
        foreach (var value in values)
        {
            sawAny = true;
            if (value > MmolCeiling) return Unit.MgDl;
        }

        return sawAny ? Unit.Mmol : Unit.MgDl;
    }

    /// <summary>
    ///     <paramref name="value"/> in whole mg/dL. A mmol/L reading was rounded to a tenth on its
    ///     way in, so the tenth-of-a-mg/dL the multiplication yields is noise, not precision.
    /// </summary>
    public static double ToMgdl(double value, Unit unit) => unit switch
    {
        Unit.Mmol => Math.Round(value * GlucoseConstants.MgdlPerMmol, MidpointRounding.AwayFromZero),
        _ => Math.Round(value, MidpointRounding.AwayFromZero),
    };

    /// <summary>
    ///     The setting the mappers should work from: an explicit choice stands; <c>Auto</c> takes
    ///     the unit the account itself reports, and only infers from the values when the profile
    ///     could not be read.
    /// </summary>
    public static string? Effective(string? setting, GlookoXtGlucoseUnitSetting? accountUnit)
    {
        var explicitUnit = setting?.Trim().ToLowerInvariant() is "mgdl" or "mg/dl" or "mmol" or "mmol/l";
        if (explicitUnit) return setting;
        return accountUnit switch
        {
            GlookoXtGlucoseUnitSetting.MgDl => GlookoXtConstants.GlucoseUnits.MgDl,
            GlookoXtGlucoseUnitSetting.Mmol => GlookoXtConstants.GlucoseUnits.Mmol,
            _ => setting,
        };
    }

    /// <summary>Whether <paramref name="setting"/> is one of the values the configuration admits.</summary>
    public static bool IsKnownSetting(string? setting) =>
        string.IsNullOrWhiteSpace(setting)
        || string.Equals(setting, GlookoXtConstants.GlucoseUnits.Auto, StringComparison.OrdinalIgnoreCase)
        || string.Equals(setting, GlookoXtConstants.GlucoseUnits.MgDl, StringComparison.OrdinalIgnoreCase)
        || string.Equals(setting, GlookoXtConstants.GlucoseUnits.Mmol, StringComparison.OrdinalIgnoreCase);
}
