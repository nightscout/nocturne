using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// Behaviour of the cross-tenant fan-out engine <see cref="ConnectorCursorResetService"/>:
/// <list type="bullet">
///   <item>it switches the tenant context to the <em>target</em> tenant (not the caller's);</item>
///   <item>it fans out across <em>every</em> connector that tenant has configured;</item>
///   <item>each per-connector sync forces a full re-pull (<see cref="SyncRequest.To"/> is set);</item>
///   <item>it reports incremental progress via <see cref="IConnectorResetProgress"/>.</item>
/// </list>
/// The EF Core in-memory provider is used so the RLS GUC (a PostgreSQL-only function) is skipped.
/// </summary>
public class ConnectorCursorResetServiceTests
{
    private readonly Guid _targetTenantId = Guid.CreateVersion7();

    private NocturneDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase($"connector-reset-engine-{Guid.NewGuid()}")
            .Options;

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

    [Fact]
    public async Task ResetTenantCursors_FansOutAcrossEveryConnector_SettingTargetTenantAndUpperBound()
    {
        var db = CreateDb();
        var captured = new List<(string ConnectorId, SyncRequest Request)>();
        var syncService = new Mock<IConnectorSyncService>();
        syncService
            .Setup(s => s.TriggerSyncAsync(It.IsAny<string>(), It.IsAny<SyncRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, SyncRequest, CancellationToken>((id, r, _) => captured.Add((id, r)))
            .ReturnsAsync(new SyncResult { Success = true });

        TenantContext? setContext = null;
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(a => a.SetTenant(It.IsAny<TenantContext>()))
            .Callback<TenantContext>(c => setContext = c);

        var service = new ConnectorCursorResetService(
            db, syncService.Object, tenantAccessor.Object,
            Mock.Of<ILogger<ConnectorCursorResetService>>());

        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = await service.ResetTenantCursorsAsync(
            _targetTenantId, from, [SyncDataType.Boluses], progress: null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Connectors.Should().HaveCount(2);

        // Fans out across BOTH configured connectors.
        captured.Select(c => c.ConnectorId).Should().BeEquivalentTo(["nightscout", "dexcom"]);

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
    public async Task ResetTenantCursors_UnknownTenant_ReturnsNull_AndDoesNotSync()
    {
        var db = CreateDb();
        var syncService = new Mock<IConnectorSyncService>();
        var service = new ConnectorCursorResetService(
            db, syncService.Object, Mock.Of<ITenantAccessor>(),
            Mock.Of<ILogger<ConnectorCursorResetService>>());

        var result = await service.ResetTenantCursorsAsync(
            Guid.CreateVersion7(), from: null, dataTypes: null, progress: null, CancellationToken.None);

        result.Should().BeNull();
        syncService.Verify(
            s => s.TriggerSyncAsync(It.IsAny<string>(), It.IsAny<SyncRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResetTenantCursors_ReportsStartedAndCompletedForEachConnector()
    {
        var db = CreateDb();
        var syncService = new Mock<IConnectorSyncService>();
        syncService
            .Setup(s => s.TriggerSyncAsync("nightscout", It.IsAny<SyncRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncResult { Success = true, Message = "ok" });
        syncService
            .Setup(s => s.TriggerSyncAsync("dexcom", It.IsAny<SyncRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("nope"));

        var service = new ConnectorCursorResetService(
            db, syncService.Object, Mock.Of<ITenantAccessor>(),
            Mock.Of<ILogger<ConnectorCursorResetService>>());

        var progress = new RecordingProgress();

        var result = await service.ResetTenantCursorsAsync(
            _targetTenantId, from: null, dataTypes: null, progress, CancellationToken.None);

        result.Should().NotBeNull();

        // Both connectors reported a start and a completion.
        progress.Started.Should().BeEquivalentTo(["nightscout", "dexcom"]);
        progress.Completed.Should().HaveCount(2);

        // A failing connector is isolated and reported as a non-success outcome, not thrown.
        progress.Completed.Should().Contain(r => r.ConnectorName == "nightscout" && r.Result.Success);
        progress.Completed.Should().Contain(r => r.ConnectorName == "dexcom" && !r.Result.Success);
    }

    private sealed class RecordingProgress : IConnectorResetProgress
    {
        public List<string> Started { get; } = [];
        public List<ConnectorCursorResetResult> Completed { get; } = [];

        public void ConnectorStarted(string connectorName) => Started.Add(connectorName);
        public void ConnectorCompleted(ConnectorCursorResetResult result) => Completed.Add(result);
    }
}
