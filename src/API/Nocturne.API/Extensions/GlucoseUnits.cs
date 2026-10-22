using Nocturne.API.Services.Auth;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Extensions;

/// <summary>
/// The tenant's glucose units as the Nightscout compatibility endpoints resolve them: the owner's
/// saved display preference, read through <see cref="ITenantAppearanceReader"/>.
/// </summary>
internal static class GlucoseUnits
{
    public static bool IsMmol(string? units) =>
        units?.StartsWith("mmol", StringComparison.OrdinalIgnoreCase) == true;

    public static async Task<bool> UsesMmolAsync(
        ITenantAppearanceReader appearanceReader,
        ITenantAccessor tenantAccessor,
        CancellationToken cancellationToken
    )
    {
        var appearance = await appearanceReader.GetTenantAppearanceAsync(
            tenantAccessor.TenantId,
            cancellationToken
        );
        return IsMmol(appearance.GlucoseUnits);
    }

    /// <summary>
    /// A mg/dL value in the tenant's units: whole mg/dL, or mmol/L to one decimal.
    /// </summary>
    public static double Scale(double mgdl, bool useMmol) =>
        useMmol ? Math.Round(mgdl / GlucoseConstants.MgdlPerMmol, 1) : Math.Round(mgdl);
}
