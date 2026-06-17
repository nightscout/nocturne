using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

[Trait("Category", "Unit")]
public class AlertAcknowledgeEndpointTests
{
    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly Mock<ITenantDbContextFactory> _contextFactoryMock = new();
    private readonly Mock<IAlertAcknowledgementService> _acknowledgementServiceMock = new();
    private readonly Mock<IAlertDeliveryService> _deliveryServiceMock = new();
    private readonly Mock<ITenantAccessor> _tenantAccessorMock = new();
    private readonly Mock<ILogger<AlertsController>> _loggerMock = new();

    private readonly Guid _tenantId = Guid.NewGuid();

    public AlertAcknowledgeEndpointTests()
    {
        _options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase($"alerts_ack_endpoint_tests_{Guid.NewGuid()}")
            .Options;
        using (var db = new NocturneDbContext(_options))
        {
            db.Database.EnsureCreated();
        }

        _tenantAccessorMock.Setup(t => t.IsResolved).Returns(true);
        _tenantAccessorMock.Setup(t => t.TenantId).Returns(_tenantId);

        // The controller disposes the context (await using), so hand out a fresh
        // tenant-scoped context per call against the shared in-memory store.
        _contextFactoryMock
            .Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                var ctx = new NocturneDbContext(_options) { TenantId = _tenantId };
                return ValueTask.FromResult(ctx);
            });
    }

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

    private async Task<Guid> SeedExcursionAsync(Guid? tenantId = null)
    {
        var t = tenantId ?? _tenantId;
        await using var db = new NocturneDbContext(_options);
        db.TenantId = t;
        var excursion = new AlertExcursionEntity
        {
            Id = Guid.NewGuid(),
            TenantId = t,
            AlertRuleId = Guid.NewGuid(),
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        db.AlertExcursions.Add(excursion);
        await db.SaveChangesAsync();
        return excursion.Id;
    }

    // ---- AcknowledgeExcursion ----

    [Fact]
    public async Task AcknowledgeExcursion_ExistingExcursion_CallsServiceAndReturnsNoContent()
    {
        // Arrange
        var excursionId = await SeedExcursionAsync();
        var controller = CreateController();
        var request = new AcknowledgeRequest { AcknowledgedBy = "user:bob" };

        // Act
        var result = await controller.AcknowledgeExcursion(excursionId, request, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        _acknowledgementServiceMock.Verify(
            s => s.AcknowledgeExcursionAsync(_tenantId, excursionId, "user:bob", true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AcknowledgeExcursion_NoAcknowledgedBy_DefaultsToUnknown()
    {
        // Arrange
        var excursionId = await SeedExcursionAsync();
        var controller = CreateController();
        var request = new AcknowledgeRequest();

        // Act
        var result = await controller.AcknowledgeExcursion(excursionId, request, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        _acknowledgementServiceMock.Verify(
            s => s.AcknowledgeExcursionAsync(_tenantId, excursionId, "unknown", true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AcknowledgeExcursion_UnknownExcursion_ReturnsNotFound()
    {
        // Arrange
        var controller = CreateController();
        var request = new AcknowledgeRequest { AcknowledgedBy = "user:bob" };

        // Act
        var result = await controller.AcknowledgeExcursion(Guid.NewGuid(), request, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
        _acknowledgementServiceMock.Verify(
            s => s.AcknowledgeExcursionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AcknowledgeExcursion_OtherTenantsExcursion_ReturnsNotFound()
    {
        // Arrange — excursion exists but belongs to a different tenant; the
        // tenant-scoped context's query filter must hide it (404, not leak).
        var excursionId = await SeedExcursionAsync(tenantId: Guid.NewGuid());
        var controller = CreateController();
        var request = new AcknowledgeRequest { AcknowledgedBy = "user:bob" };

        // Act
        var result = await controller.AcknowledgeExcursion(excursionId, request, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
        _acknowledgementServiceMock.Verify(
            s => s.AcknowledgeExcursionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ---- Acknowledge (all) ----

    [Fact]
    public async Task Acknowledge_CallsServiceForTenant_AndReturnsNoContent()
    {
        // Arrange
        var controller = CreateController();
        var request = new AcknowledgeRequest { AcknowledgedBy = "user:bob" };

        // Act
        var result = await controller.Acknowledge(request, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        _acknowledgementServiceMock.Verify(
            s => s.AcknowledgeAllAsync(_tenantId, "user:bob", It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
