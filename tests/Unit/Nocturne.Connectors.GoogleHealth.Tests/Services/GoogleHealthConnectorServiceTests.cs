using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Models.Health;
using Xunit;

namespace Nocturne.Connectors.GoogleHealth.Tests.Services;

public class GoogleHealthConnectorServiceTests
{
    [Fact]
    public async Task Background_sync_does_not_run_without_an_oauth_session()
    {
        var googleHealth = new Mock<IGoogleHealthService>();
        googleHealth.Setup(service => service.StatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GoogleHealthStatus { Configured = true });
        var service = CreateService(googleHealth.Object);

        var result = await service.SyncDataAsync(
            new GoogleHealthConnectorConfiguration(),
            CancellationToken.None);

        Assert.False(result.Success);
        googleHealth.Verify(value => value.SyncAsync(true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("partial_consent", true)]
    [InlineData("google_unavailable", false)]
    public async Task Sync_uses_the_stored_connector_outcome(string? errorCode, bool expectedSuccess)
    {
        var googleHealth = new Mock<IGoogleHealthService>();
        googleHealth.Setup(service => service.StatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GoogleHealthStatus
            {
                Configured = true,
                Connected = true,
                ErrorCode = errorCode
            });
        var service = CreateService(googleHealth.Object);

        var result = await service.SyncDataAsync(
            new SyncRequest(),
            new GoogleHealthConnectorConfiguration(),
            CancellationToken.None);

        Assert.Equal(expectedSuccess, result.Success);
        Assert.Equal(expectedSuccess ? Array.Empty<string>() : new[] { errorCode! }, result.Errors);
        googleHealth.Verify(value => value.SyncAsync(true, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static GoogleHealthConnectorService CreateService(IGoogleHealthService googleHealth) => new(
        new HttpClient(),
        new ConnectorServerResolver<GoogleHealthConnectorConfiguration>(null, null, null),
        googleHealth,
        NullLogger<GoogleHealthConnectorService>.Instance);
}
