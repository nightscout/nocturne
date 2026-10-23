using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Analytics;

public class TenantClockResolverTests
{
    [Fact]
    public async Task AShare_GetsNoZoneWithoutALookup_EvenWhenOneIsCached()
    {
        var settings = new Mock<ITherapySettingsResolver>();
        settings.Setup(s => s.GetTimezoneAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync("Australia/Sydney");
        var share = new CategoryReadContext();
        share.MarkShare();

        var clock = await new TenantClockResolver(settings.Object, share, NullLogger<TenantClockResolver>.Instance)
            .ResolveAsync(CancellationToken.None);

        clock.Should().Be(new TenantClock(null, TimeZoneUnavailableReason.Share));
        settings.Verify(s => s.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AMember_GetsTheStoredZone()
    {
        var settings = new Mock<ITherapySettingsResolver>();
        settings.Setup(s => s.GetTimezoneAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync("Australia/Sydney");

        var clock = await new TenantClockResolver(settings.Object, new CategoryReadContext(), NullLogger<TenantClockResolver>.Instance)
            .ResolveAsync(CancellationToken.None);

        clock.Zone!.Id.Should().Be("Australia/Sydney");
        clock.Reason.Should().BeNull();
    }
}
