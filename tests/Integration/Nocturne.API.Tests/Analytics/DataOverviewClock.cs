using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Integration.Analytics;

internal static class DataOverviewClock
{
    /// <summary>
    /// A clock that always resolves <paramref name="timezone"/>. A real share never gets a zone,
    /// because <see cref="TenantClockResolver"/> answers it without a lookup, so the share and
    /// clamp rows that use this exercise row level security with a zone no share can have.
    /// </summary>
    internal static TenantClockResolver Resolving(string timezone)
    {
        var settings = new Mock<ITherapySettingsResolver>();
        settings
            .Setup(resolver => resolver.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(timezone);
        return new TenantClockResolver(settings.Object, new CategoryReadContext(), NullLogger<TenantClockResolver>.Instance);
    }
}
