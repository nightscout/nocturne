using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
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
    private readonly Guid _subjectId = Guid.NewGuid();

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
            Mock.Of<IAlertSnoozeService>(),
            _tenantAccessorMock.Object,
            _loggerMock.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.SetAuthContext(new AuthContext { IsAuthenticated = true, SubjectId = _subjectId });
        httpContext.SetGrantedScopes(new HashSet<string> { Scope.DeviceNotify });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

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
    public async Task AcknowledgeExcursion_ExistingExcursion_PassesCallerAuthorityAndReturnsOutcome()
    {
        var excursionId = await SeedExcursionAsync();
        var controller = CreateController();
        _acknowledgementServiceMock
            .Setup(s => s.AcknowledgeExcursionAsync(
                _tenantId, excursionId, It.IsAny<string>(), It.IsAny<AlertAcknowledgementAuthority>(),
                true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AlertAcknowledgementOutcome.Muted);

        var result = await controller.AcknowledgeExcursion(
            excursionId, new AcknowledgeRequest(), CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<AcknowledgeExcursionResponse>()
            .Which.Outcome.Should().Be(AlertAcknowledgementOutcome.Muted);
        _acknowledgementServiceMock.Verify(
            s => s.AcknowledgeExcursionAsync(
                _tenantId,
                excursionId,
                It.IsAny<string>(),
                It.Is<AlertAcknowledgementAuthority>(a =>
                    a.SubjectId == _subjectId && a.GrantedScopes.SetEquals(new[] { Scope.DeviceNotify })),
                true,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AcknowledgeExcursion_MachineCallerWithNoNameClaim_UsesRequestLabel()
    {
        var excursionId = await SeedExcursionAsync();
        var controller = CreateController();

        await controller.AcknowledgeExcursion(
            excursionId, new AcknowledgeRequest { AcknowledgedBy = "user:bob" }, CancellationToken.None);

        _acknowledgementServiceMock.Verify(
            s => s.AcknowledgeExcursionAsync(
                _tenantId, excursionId, "user:bob", It.IsAny<AlertAcknowledgementAuthority>(),
                true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AcknowledgeExcursion_NoAcknowledgedBy_DefaultsToUnknown()
    {
        var excursionId = await SeedExcursionAsync();
        var controller = CreateController();

        await controller.AcknowledgeExcursion(excursionId, new AcknowledgeRequest(), CancellationToken.None);

        _acknowledgementServiceMock.Verify(
            s => s.AcknowledgeExcursionAsync(
                _tenantId, excursionId, "unknown", It.IsAny<AlertAcknowledgementAuthority>(),
                true, It.IsAny<CancellationToken>()),
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
        result.Result.Should().BeOfType<NotFoundResult>();
        _acknowledgementServiceMock.Verify(
            s => s.AcknowledgeExcursionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<AlertAcknowledgementAuthority>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()),
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
        result.Result.Should().BeOfType<NotFoundResult>();
        _acknowledgementServiceMock.Verify(
            s => s.AcknowledgeExcursionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<AlertAcknowledgementAuthority>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()),
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
