using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Timezones;
using Nocturne.Core.Models.Timezones;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>
/// Stands in for the scope that reads as no one: a share reads the same zone and timeline as a member.
/// </summary>
internal sealed class ShareClockDouble(ITherapySettingsResolver therapy, ITimezoneTimelineService? timelines = null)
    : IShareClockReader
{
    public Task<string?> ReadTimezoneAsync(CancellationToken ct) => therapy.GetTimezoneAsync(ct: ct);

    public Task<TimezoneTimeline> ReadTimelineAsync(CancellationToken ct) =>
        timelines?.GetResolverAsync(fallbackOffsetHours: null, ct) ?? Task.FromResult(new TimezoneTimeline([]));
}
