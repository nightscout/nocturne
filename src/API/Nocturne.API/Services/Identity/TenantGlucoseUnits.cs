using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// The glucose units of the current tenant: the owner's saved display units, else the default the
/// owner chose in onboarding, else mg/dL.
/// </summary>
/// <remarks>
/// A member's own choice is not read here. The member's session carries the onboarding default
/// beside their own preferences (<see cref="UnitsAndTimezoneService"/>).
/// </remarks>
public interface ITenantGlucoseUnits
{
    /// <summary>"mg/dl" or "mmol", as <see cref="GlucoseUnitDefaults"/> spells them.</summary>
    Task<string> ResolveAsync(CancellationToken ct = default);

    Task<bool> UsesMmolAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public class TenantGlucoseUnits(
    ITenantAccessor tenantAccessor,
    ITenantAppearanceReader appearanceReader,
    ITenantService tenants) : ITenantGlucoseUnits
{
    public async Task<string> ResolveAsync(CancellationToken ct = default)
    {
        var appearance = await appearanceReader.GetTenantAppearanceAsync(tenantAccessor.TenantId, ct);
        if (appearance.GlucoseUnits is { } saved)
            return saved;

        var setupDefault = tenantAccessor.Context is { } tenant
            ? await tenants.GetDefaultGlucoseUnitsAsync(tenant.TenantId, ct)
            : null;
        return setupDefault ?? GlucoseUnitDefaults.MgDl;
    }

    public async Task<bool> UsesMmolAsync(CancellationToken ct = default) =>
        GlucoseUnits.IsMmol(await ResolveAsync(ct));
}
