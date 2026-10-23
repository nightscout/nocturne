using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Timezones;
using Nocturne.Core.Models.Timezones;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.TestDoubles;

internal static class DeviceDateClockDoubles
{
    /// <summary>No timeline and no profile zone: every instant resolves to its UTC date.</summary>
    public static DeviceDateClockResolver Utc() => Resolver();

    /// <summary>A timeline whose one entry puts every instant in <paramref name="ianaZone"/>.</summary>
    public static DeviceDateClockResolver Timeline(string ianaZone) =>
        Resolver(timeline: [new TimezoneTimelineEntry { Id = Guid.NewGuid(), EffectiveFrom = DateTime.MinValue, Timezone = ianaZone }]);

    public static DeviceDateClockResolver Resolver(string? profileZone = null, IReadOnlyList<TimezoneTimelineEntry>? timeline = null, CategoryReadContext? readContext = null)
    {
        var therapy = new Mock<ITherapySettingsResolver>();
        therapy
            .Setup(t => t.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(profileZone);

        return new DeviceDateClockResolver(
            Timelines(timeline ?? []),
            new TenantClockResolver(therapy.Object, readContext ?? new CategoryReadContext(), NullLogger<TenantClockResolver>.Instance));
    }

    public static ITimezoneTimelineService Timelines(IReadOnlyList<TimezoneTimelineEntry> entries)
    {
        var timelines = new Mock<ITimezoneTimelineService>();
        timelines
            .Setup(s => s.GetResolverAsync(It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TimezoneTimeline(entries));
        return timelines.Object;
    }
}
