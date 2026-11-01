using Nocturne.Core.Constants;

namespace Nocturne.API.Extensions;

/// <summary>
/// Reads and scales glucose units. The tenant's own are resolved by <see cref="Services.Identity.ITenantGlucoseUnits"/>.
/// </summary>
internal static class GlucoseUnits
{
    public static bool IsMmol(string? units) =>
        units?.StartsWith("mmol", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// A mg/dL value in the tenant's units: whole mg/dL, or mmol/L to one decimal.
    /// </summary>
    public static double Scale(double mgdl, bool useMmol) =>
        useMmol ? Math.Round(mgdl / GlucoseConstants.MgdlPerMmol, 1) : Math.Round(mgdl);
}
