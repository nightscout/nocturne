using FluentAssertions;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Timezones;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Devices;

[Trait("Category", "Unit")]
public class DeviceDateClockResolverTests
{
    [Fact]
    public async Task AShare_ReadsNoTimeline_SoItsDeviceDatesAreUtcDays()
    {
        var share = new CategoryReadContext();
        share.MarkShare();
        var resolver = DeviceDateClockDoubles.Resolver(
            profileZone: "Australia/Adelaide",
            timeline: [new TimezoneTimelineEntry { EffectiveFrom = DateTime.MinValue, Timezone = "Australia/Adelaide" }],
            readContext: share);

        var clock = await resolver.ResolveAsync(CancellationToken.None);

        clock.Reason.Should().Be(TimeZoneUnavailableReason.Share);
        clock.LocalDate(new DateTime(2026, 6, 10, 15, 0, 0, DateTimeKind.Utc)).Should().Be(new DateOnly(2026, 6, 10));
    }
}
