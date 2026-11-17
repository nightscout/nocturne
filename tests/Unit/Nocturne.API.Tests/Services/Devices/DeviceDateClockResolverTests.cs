using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Devices;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Timezones;
using Nocturne.Core.Models.Timezones;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Devices;

[Trait("Category", "Unit")]
public class DeviceDateClockResolverTests
{
    [Fact]
    public async Task AShare_ReadsTheTimelineThroughTheShareClockReader_SoItsDeviceDatesAreLocalDays()
    {
        var share = new CategoryReadContext();
        share.MarkShare();
        var therapy = new Mock<ITherapySettingsResolver>();
        therapy.Setup(t => t.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("UTC");
        var reader = new ShareClockDouble(
            therapy.Object,
            DeviceDateClockDoubles.Timelines(
                [new TimezoneTimelineEntry { EffectiveFrom = DateTime.MinValue, Timezone = "Australia/Adelaide" }]));
        var resolver = new DeviceDateClockResolver(
            new Mock<ITimezoneTimelineService>(MockBehavior.Strict).Object,
            new TenantClockResolver(therapy.Object, share, reader, NullLogger<TenantClockResolver>.Instance),
            share,
            reader);

        var clock = await resolver.ResolveAsync(CancellationToken.None);

        clock.Reason.Should().BeNull();
        clock.LocalDate(new DateTime(2026, 6, 10, 15, 0, 0, DateTimeKind.Utc)).Should().Be(new DateOnly(2026, 6, 11));
    }
}
