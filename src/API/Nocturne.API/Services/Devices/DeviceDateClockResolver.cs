using Nocturne.API.Services.Analytics;
using Nocturne.Core.Clinical.Devices;
using Nocturne.Core.Contracts.Timezones;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Timezones;

namespace Nocturne.API.Services.Devices;

/// <summary>
/// Resolves the <see cref="DeviceDateClock"/> that every reader of a registered device's dates goes
/// through: matching a record to a device at ingest, the AID system metrics and Loop Analysis.
/// </summary>
/// <remarks>
/// Both sources are read through the scope's tenant, so this works in an ingest scope that reads as
/// no one. A share reads no timeline: <see cref="TenantClockResolver"/> gives it no zone, and its
/// device dates are UTC days.
/// </remarks>
public sealed class DeviceDateClockResolver(ITimezoneTimelineService timelines, TenantClockResolver tenantClock)
{
    public async Task<DeviceDateClock> ResolveAsync(CancellationToken ct)
    {
        var profile = await tenantClock.ResolveAsync(ct);
        var timeline = profile.Reason == TimeZoneUnavailableReason.Share
            ? new TimezoneTimeline([])
            : await timelines.GetResolverAsync(fallbackOffsetHours: null, ct);
        return new DeviceDateClock(timeline, profile.Zone, profile.Reason);
    }
}
