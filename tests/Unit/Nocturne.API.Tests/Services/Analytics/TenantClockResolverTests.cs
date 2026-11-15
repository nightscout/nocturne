using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Analytics;

public class TenantClockResolverTests
{
    [Fact]
    public async Task AShare_ReadsTheZoneThroughTheShareClockReader()
    {
        var settings = new Mock<ITherapySettingsResolver>(MockBehavior.Strict);
        var reader = new Mock<IShareClockReader>();
        reader.Setup(r => r.ReadTimezoneAsync(It.IsAny<CancellationToken>())).ReturnsAsync("Australia/Sydney");
        var share = new CategoryReadContext();
        share.MarkShare();

        var clock = await new TenantClockResolver(settings.Object, share, reader.Object, NullLogger<TenantClockResolver>.Instance)
            .ResolveAsync(CancellationToken.None);

        clock.Zone!.Id.Should().Be("Australia/Sydney");
        clock.Reason.Should().BeNull();
    }

    [Fact]
    public async Task AMember_GetsTheStoredZone_WithoutTheShareClockReader()
    {
        var settings = new Mock<ITherapySettingsResolver>();
        settings.Setup(s => s.GetTimezoneAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync("Australia/Sydney");
        var reader = new Mock<IShareClockReader>(MockBehavior.Strict);

        var clock = await new TenantClockResolver(settings.Object, new CategoryReadContext(), reader.Object, NullLogger<TenantClockResolver>.Instance)
            .ResolveAsync(CancellationToken.None);

        clock.Zone!.Id.Should().Be("Australia/Sydney");
        clock.Reason.Should().BeNull();
    }
}
