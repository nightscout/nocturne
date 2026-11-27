using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Hubs;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Providers;

/// <summary>
/// A failed send through the hub reaches the caller, which logs it once and records the delivery
/// failed.
/// </summary>
[Trait("Category", "Unit")]
public class HomeAssistantProviderTests
{
    private static readonly Guid Tenant = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private const string Instance = "ha-instance";

    private static AlertPayload Payload() => new()
    {
        AlertType = AlertConditionType.Threshold,
        RuleName = "Low",
        Severity = AlertRuleSeverity.Critical,
        TenantId = Tenant,
        GlucoseValue = 52,
        Trend = null,
        TrendRate = null,
        ReadingTimestamp = DateTime.UtcNow,
        SubjectName = "Synthetic",
        ExcursionId = Guid.NewGuid(),
        InstanceId = Guid.NewGuid(),
        ActiveExcursionCount = 1,
    };

    private static HomeAssistantProvider ProviderWhoseSendThrows(Exception failure, ILogger<HomeAssistantProvider> logger)
    {
        var proxy = new Mock<IClientProxy>();
        proxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var hub = new Mock<IHubContext<HomeAssistantHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);
        var connections = new HomeAssistantConnections();
        connections.Add(Tenant, Instance, "connection-1");
        return new HomeAssistantProvider(hub.Object, connections, logger);
    }

    [Theory]
    [InlineData(typeof(NpgsqlException))]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NullReferenceException))]
    public async Task SendAsync_AFailureReachesTheCallerAndIsLoggedByNoOneHere(Type failureType)
    {
        var failure = (Exception)Activator.CreateInstance(failureType, "hub down")!;
        var logger = new ListLogger<HomeAssistantProvider>();

        var send = () => ProviderWhoseSendThrows(failure, logger).SendAsync(Tenant, Instance, Payload(), CancellationToken.None);

        (await send.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error, "the delivery service logs the failure once");
    }
}
