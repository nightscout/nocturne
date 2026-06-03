using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.PlatformAdmin;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.PlatformAdmin;

/// <summary>
/// Tests for the platform-admin cross-tenant connector controller. The reset endpoint enqueues a
/// background job and returns <c>202 Accepted</c> (the fan-out itself is covered by
/// <see cref="ConnectorCursorResetServiceTests"/> and <see cref="ConnectorCursorResetJobServiceTests"/>);
/// the <c>GetTenantConnectors</c> endpoint is exercised against the real engine on the EF Core
/// in-memory provider, where the RLS GUC (a PostgreSQL-only function) is skipped.
/// </summary>
public class ConnectorAdminControllerTests
{
    private readonly Guid _targetTenantId = Guid.CreateVersion7();

    private NocturneDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase($"connector-admin-{Guid.NewGuid()}")
            .Options;

        // The DbContext's TenantId drives the per-tenant global query filter, so it must match the
        // target tenant for that tenant's connector configurations to be visible.
        var db = new NocturneDbContext(options) { TenantId = _targetTenantId };

        db.Tenants.Add(new TenantEntity
        {
            Id = _targetTenantId,
            Slug = "erik",
            DisplayName = "Erik",
            IsActive = true,
        });

        db.ConnectorConfigurations.AddRange(
            new ConnectorConfigurationEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = _targetTenantId,
                ConnectorName = "nightscout",
                IsHealthy = true,
            },
            new ConnectorConfigurationEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = _targetTenantId,
                ConnectorName = "dexcom",
                IsHealthy = false,
                LastErrorMessage = "boom",
            });

        db.SaveChanges();
        return db;
    }

    private static ConnectorAdminController CreateController(
        IConnectorCursorResetService service,
        IConnectorCursorResetJobService? jobService = null) =>
        new(service, jobService ?? Mock.Of<IConnectorCursorResetJobService>(),
            Mock.Of<ILogger<ConnectorAdminController>>());

    private static ConnectorCursorResetService Engine(NocturneDbContext db) =>
        new(db, Mock.Of<IConnectorSyncService>(), Mock.Of<ITenantAccessor>(),
            Mock.Of<ILogger<ConnectorCursorResetService>>());

    [Fact]
    public async Task ResetTenantCursors_KnownTenant_Returns202WithJobInfo()
    {
        var db = CreateDb();
        var jobInfo = new ConnectorResetJobInfo
        {
            JobId = Guid.CreateVersion7(),
            TenantId = _targetTenantId,
            TenantSlug = "erik",
            CreatedAt = DateTime.UtcNow,
            State = ConnectorResetJobState.Pending,
            TotalConnectors = 2,
        };

        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var jobService = new Mock<IConnectorCursorResetJobService>();
        jobService
            .Setup(s => s.StartResetAsync(_targetTenantId, from,
                It.Is<List<SyncDataType>>(d => d.Contains(SyncDataType.Boluses)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobInfo);

        var controller = CreateController(Engine(db), jobService.Object);

        var result = await controller.ResetTenantCursors(
            _targetTenantId,
            new AdminResetCursorsRequest { From = from, DataTypes = [SyncDataType.Boluses] },
            CancellationToken.None);

        var accepted = result.Result.Should().BeOfType<AcceptedAtActionResult>().Subject;
        accepted.ActionName.Should().Be(nameof(ConnectorAdminController.GetResetJobStatus));
        accepted.Value.Should().BeSameAs(jobInfo);
    }

    [Fact]
    public async Task ResetTenantCursors_UnknownTenant_Returns404()
    {
        var db = CreateDb();
        var jobService = new Mock<IConnectorCursorResetJobService>();
        jobService
            .Setup(s => s.StartResetAsync(It.IsAny<Guid>(), It.IsAny<DateTime?>(),
                It.IsAny<List<SyncDataType>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConnectorResetJobInfo?)null);

        var controller = CreateController(Engine(db), jobService.Object);

        var result = await controller.ResetTenantCursors(
            Guid.CreateVersion7(), new AdminResetCursorsRequest(), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public void GetResetJobStatus_UnknownJob_Returns404()
    {
        var db = CreateDb();
        var jobService = new Mock<IConnectorCursorResetJobService>();
        jobService.Setup(s => s.GetStatus(It.IsAny<Guid>()))
            .Throws(new KeyNotFoundException());

        var controller = CreateController(Engine(db), jobService.Object);

        var result = controller.GetResetJobStatus(Guid.CreateVersion7());

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public void GetResetJobStatus_KnownJob_ReturnsStatus()
    {
        var db = CreateDb();
        var jobId = Guid.CreateVersion7();
        var status = new ConnectorResetJobStatus
        {
            JobId = jobId,
            TenantId = _targetTenantId,
            TenantSlug = "erik",
            State = ConnectorResetJobState.Running,
        };
        var jobService = new Mock<IConnectorCursorResetJobService>();
        jobService.Setup(s => s.GetStatus(jobId)).Returns(status);

        var controller = CreateController(Engine(db), jobService.Object);

        var result = controller.GetResetJobStatus(jobId);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeSameAs(status);
    }

    [Fact]
    public void CancelResetJob_UnknownJob_Returns404()
    {
        var db = CreateDb();
        var jobService = new Mock<IConnectorCursorResetJobService>();
        jobService.Setup(s => s.Cancel(It.IsAny<Guid>())).Throws(new KeyNotFoundException());

        var controller = CreateController(Engine(db), jobService.Object);

        var result = controller.CancelResetJob(Guid.CreateVersion7());

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public void CancelResetJob_KnownJob_Returns204()
    {
        var db = CreateDb();
        var jobId = Guid.CreateVersion7();
        var jobService = new Mock<IConnectorCursorResetJobService>();

        var controller = CreateController(Engine(db), jobService.Object);

        var result = controller.CancelResetJob(jobId);

        result.Should().BeOfType<NoContentResult>();
        jobService.Verify(s => s.Cancel(jobId), Times.Once);
    }

    [Fact]
    public async Task GetTenantConnectors_ReturnsConfiguredConnectorsWithHealth()
    {
        var db = CreateDb();
        var controller = CreateController(Engine(db));

        var result = await controller.GetTenantConnectors(_targetTenantId, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<TenantConnectorsDto>().Subject;
        payload.TenantSlug.Should().Be("erik");
        payload.Connectors.Should().HaveCount(2);
        payload.Connectors.Should().Contain(c => c.ConnectorName == "dexcom" && !c.IsHealthy && c.LastErrorMessage == "boom");
    }

    [Fact]
    public async Task GetTenantConnectors_UnknownTenant_Returns404()
    {
        var db = CreateDb();
        var controller = CreateController(Engine(db));

        var result = await controller.GetTenantConnectors(Guid.CreateVersion7(), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }
}
