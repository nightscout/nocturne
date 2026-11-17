using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Timezones;
using Nocturne.Core.Models.Timezones;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Analytics;

/// <summary>Reads the tenant's timezone and timezone timeline for a public share.</summary>
/// <remarks>
/// The zone lives in the patient record, therapy settings, profile activations and the timeline, and no
/// share category governs them, so RLS hides them from a share reader. Without the zone, every report
/// that places data on local days or hours is off for a share. So each read runs in a scope that reads
/// as no one and returns only the zone id or the timeline's entries, never the rows they come from.
/// </remarks>
public interface IShareClockReader
{
    /// <summary>The tenant's current timezone id, by <see cref="ITherapySettingsResolver.GetTimezoneAsync"/>.</summary>
    Task<string?> ReadTimezoneAsync(CancellationToken ct);

    /// <summary>The tenant's timezone timeline, with no fallback offset.</summary>
    Task<TimezoneTimeline> ReadTimelineAsync(CancellationToken ct);
}

/// <inheritdoc cref="IShareClockReader"/>
internal sealed class ShareClockReader(IServiceProvider services, ITenantAccessor tenants) : IShareClockReader
{
    private const string Actor = "share:clock";

    public async Task<string?> ReadTimezoneAsync(CancellationToken ct)
    {
        await using var scope = TenantJobScope.Open(services, tenants.Context, Actor);
        return await scope.ServiceProvider.GetRequiredService<ITherapySettingsResolver>().GetTimezoneAsync(ct: ct);
    }

    public async Task<TimezoneTimeline> ReadTimelineAsync(CancellationToken ct)
    {
        await using var scope = TenantJobScope.Open(services, tenants.Context, Actor);
        return await scope.ServiceProvider.GetRequiredService<ITimezoneTimelineService>()
            .GetResolverAsync(fallbackOffsetHours: null, ct);
    }
}
