using Nocturne.Core.Constants;

namespace Nocturne.Core.Clinical.Schedules;

/// <summary>
/// The units a temporary target's bounds were uploaded in. Uploaders store them as entered, in
/// either unit; Nightscout reads a target as mmol/L when its <c>units</c> say so or when either
/// bound is below 20, which no mg/dL target can be. The units string is compared exactly, so a
/// target labelled "mmol/L" with a bound of 20 or more is a mislabelled mg/dL value and stays
/// unconverted, as Nightscout serves it.
/// </summary>
public static class TemporaryTargetUnits
{
    private const double MmolCeiling = 20;

    public static bool IsMmol(double? top, double? bottom, string? units) =>
        units == "mmol" || top < MmolCeiling || bottom < MmolCeiling;

    public static (double? Top, double? Bottom) ToMgdl(double? top, double? bottom, string? units) =>
        IsMmol(top, bottom, units)
            ? (top * GlucoseConstants.MgdlPerMmol, bottom * GlucoseConstants.MgdlPerMmol)
            : (top, bottom);
}
