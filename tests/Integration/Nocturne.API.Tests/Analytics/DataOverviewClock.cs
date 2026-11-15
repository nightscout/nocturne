using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Integration.Analytics;

internal static class DataOverviewClock
{
    /// <summary>A clock that always resolves <paramref name="timezone"/>, for a member and a share alike.</summary>
    internal static TenantClockResolver Resolving(string timezone)
    {
        var settings = new Mock<ITherapySettingsResolver>();
        settings
            .Setup(resolver => resolver.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(timezone);
        var share = new Mock<IShareClockReader>();
        share.Setup(reader => reader.ReadTimezoneAsync(It.IsAny<CancellationToken>())).ReturnsAsync(timezone);
        return new TenantClockResolver(
            settings.Object, new CategoryReadContext(), share.Object, NullLogger<TenantClockResolver>.Instance);
    }
}
