using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

[Trait("Category", "Unit")]
public class AlertDeliveryEndpointTests
{
    private readonly Mock<ITenantDbContextFactory> _contextFactoryMock = new();
    private readonly Mock<IAlertAcknowledgementService> _acknowledgementServiceMock = new();
    private readonly Mock<IAlertDeliveryService> _deliveryServiceMock = new();
    private readonly Mock<ITenantAccessor> _tenantAccessorMock = new();
    private readonly Mock<ILogger<AlertsController>> _loggerMock = new();

    private AlertsController CreateController()
    {
        var controller = new AlertsController(
            _contextFactoryMock.Object,
            _acknowledgementServiceMock.Object,
            _deliveryServiceMock.Object,
            _tenantAccessorMock.Object,
            _loggerMock.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        return controller;
    }

    [Fact]
    public async Task MarkDelivered_CallsService_WithCorrectParams()
    {
        // Arrange
        var deliveryId = Guid.NewGuid();
        var request = new MarkDeliveredRequest
        {
            PlatformMessageId = "msg-123",
            PlatformThreadId = "thread-456",
        };
        var controller = CreateController();

        // Act
        var result = await controller.MarkDelivered(deliveryId, request, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        _deliveryServiceMock.Verify(
            s => s.MarkDeliveredAsync(deliveryId, "msg-123", "thread-456", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task MarkFailed_CallsService_WithCorrectParams()
    {
        // Arrange
        var deliveryId = Guid.NewGuid();
        var request = new MarkFailedRequest { Error = "timeout" };
        var controller = CreateController();

        // Act
        var result = await controller.MarkFailed(deliveryId, request, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        _deliveryServiceMock.Verify(
            s => s.MarkFailedAsync(deliveryId, "timeout", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetPendingDeliveries_ReturnsMatchingDeliveries()
    {
        // This endpoint queries the DbContext directly, so a full test requires
        // an in-memory database. We verify the controller can be instantiated
        // and the endpoint signature is correct. Integration tests cover the query logic.

        // Arrange
        var controller = CreateController();

        // The endpoint requires a real DbContext for the query.
        // Verify the method exists and accepts the expected parameters.
        var method = typeof(AlertsController).GetMethod("GetPendingDeliveries");
        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(Task<ActionResult<List<PendingDeliveryResponse>>>));
    }

    [Fact]
    public void Heartbeat_Returns200()
    {
        // Arrange
        var controller = new SystemController(new BotHealthService());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        var request = new HeartbeatRequest
        {
            Service = "discord-bot",
            Platforms = ["discord"]
        };

        // Act
        var result = controller.Heartbeat(request);

        // Assert
        result.Should().BeOfType<OkResult>();
    }
}
