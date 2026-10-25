using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.TestDoubles;

internal static class TenantClockDoubles
{
    /// <summary>A clock whose tenant zone is <paramref name="ianaZone"/>, or none when it is null.</summary>
    public static TenantClockResolver In(string? ianaZone, Mock<ITherapySettingsResolver>? therapy = null)
    {
        therapy ??= new Mock<ITherapySettingsResolver>();
        therapy
            .Setup(t => t.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ianaZone);

        return new TenantClockResolver(therapy.Object, new CategoryReadContext(), NullLogger<TenantClockResolver>.Instance);
    }
}
