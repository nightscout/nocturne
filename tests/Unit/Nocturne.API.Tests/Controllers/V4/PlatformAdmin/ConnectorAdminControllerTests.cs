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
/// Tests for the platform-admin cross-tenant cursor-reset endpoint and its backing service.
/// The key behaviours are:
/// <list type="bullet">
///   <item>the action sets the tenant context to the <em>target</em> tenant (not the caller's);</item>
///   <item>it fans out across <em>every</em> connector that tenant has configured;</item>
///   <item>each per-connector sync forces a full re-pull (<see cref="SyncRequest.To"/> is set).</item>
/// </list>
/// The EF Core in-memory provider is used so the RLS GUC (a PostgreSQL-only function) is skipped.
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

    private static ConnectorAdminController CreateController(IConnectorCursorResetService service) =>
        new(service, Mock.Of<ILogger<ConnectorAdminController>>());

    [Fact]
    public async Task ResetTenantCursors_FansOutAcrossEveryConnector_WithUpperBoundSet()
    {
        var db = CreateDb();
        var captured = new List<(string ConnectorId, SyncRequest Request)>();
        var syncService = new Mock<IConnectorSyncService>();
        syncService
            .Setup(s => s.TriggerSyncAsync(It.IsAny<string>(), It.IsAny<SyncRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, SyncRequest, CancellationToken>((id, r, _) => captured.Add((id, r)))
            .ReturnsAsync(new SyncResult { Success = true });

        var tenantAccessor = new Mock<ITenantAccessor>();
        TenantContext? setContext = null;
        tenantAccessor.Setup(a => a.SetTenant(It.IsAny<TenantContext>()))
            .Callback<TenantContext>(c => setContext = c);

        var service = new ConnectorCursorResetService(
            db, syncService.Object, tenantAccessor.Object,
            Mock.Of<ILogger<ConnectorCursorResetService>>());
        var controller = CreateController(service);

        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = await controller.ResetTenantCursors(
            _targetTenantId,
            new AdminResetCursorsRequest { From = from, DataTypes = [SyncDataType.Boluses] },
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<TenantCursorResetResult>().Subject;

        // Fans out across BOTH configured connectors.
        captured.Should().HaveCount(2);
        captured.Select(c => c.ConnectorId).Should().BeEquivalentTo(["nightscout", "dexcom"]);
        payload.Connectors.Should().HaveCount(2);

        // Every request forces a full re-pull (To set), passing through the requested filters.
        captured.Should().OnlyContain(c => c.Request.To != null);
        captured.Should().OnlyContain(c => c.Request.From == from);
        captured.Should().OnlyContain(c => c.Request.DataTypes.Contains(SyncDataType.Boluses));

        // The tenant context is switched to the TARGET tenant on the admin's behalf.
        setContext.Should().NotBeNull();
        setContext!.TenantId.Should().Be(_targetTenantId);
        setContext.Slug.Should().Be("erik");
    }

    [Fact]
    public async Task ResetTenantCursors_UnknownTenant_Returns404_AndDoesNotSync()
    {
        var db = CreateDb();
        var syncService = new Mock<IConnectorSyncService>();
        var service = new ConnectorCursorResetService(
            db, syncService.Object, Mock.Of<ITenantAccessor>(),
            Mock.Of<ILogger<ConnectorCursorResetService>>());
        var controller = CreateController(service);

        var result = await controller.ResetTenantCursors(
            Guid.CreateVersion7(), new AdminResetCursorsRequest(), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
        syncService.Verify(
            s => s.TriggerSyncAsync(It.IsAny<string>(), It.IsAny<SyncRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetTenantConnectors_ReturnsConfiguredConnectorsWithHealth()
    {
        var db = CreateDb();
        var service = new ConnectorCursorResetService(
            db, Mock.Of<IConnectorSyncService>(), Mock.Of<ITenantAccessor>(),
            Mock.Of<ILogger<ConnectorCursorResetService>>());
        var controller = CreateController(service);

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
        var service = new ConnectorCursorResetService(
            db, Mock.Of<IConnectorSyncService>(), Mock.Of<ITenantAccessor>(),
            Mock.Of<ILogger<ConnectorCursorResetService>>());
        var controller = CreateController(service);

        var result = await controller.GetTenantConnectors(Guid.CreateVersion7(), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }
}
