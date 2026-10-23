using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Analytics;

/// <summary>The tenant's timezone, or no zone and why not.</summary>
public readonly record struct TenantClock(TimeZoneInfo? Zone, TimeZoneUnavailableReason? Reason);

/// <summary>
/// Resolves the tenant's timezone for the reports that place readings on its local clock: none is
/// set, the stored one does not resolve, the request is a public share, or the lookup threw.
/// </summary>
/// <remarks>
/// A share gets no zone and the reason <see cref="TimeZoneUnavailableReason.Share"/> without a
/// lookup. The zone is a therapy setting, and therapy is not a share category, so a share can never
/// read it.
/// </remarks>
public sealed class TenantClockResolver(
    ITherapySettingsResolver therapySettingsResolver,
    ICategoryReadContext categoryReadContext,
    ILogger<TenantClockResolver> logger)
{
    public async Task<TenantClock> ResolveAsync(CancellationToken ct)
    {
        if (categoryReadContext.IsShare)
            return new TenantClock(null, TimeZoneUnavailableReason.Share);

        try
        {
            var id = await therapySettingsResolver.GetTimezoneAsync(ct: ct);
            if (TimeZoneHelper.TryGetTimeZoneInfoFromId(id, out var tz))
                return new TenantClock(tz, null);

            return new TenantClock(
                null,
                string.IsNullOrWhiteSpace(id)
                    ? TimeZoneUnavailableReason.NotConfigured
                    : TimeZoneUnavailableReason.Unrecognised);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Tenant timezone lookup failed; reports go without the tenant's zone");
            return new TenantClock(null, TimeZoneUnavailableReason.LookupFailed);
        }
    }
}
