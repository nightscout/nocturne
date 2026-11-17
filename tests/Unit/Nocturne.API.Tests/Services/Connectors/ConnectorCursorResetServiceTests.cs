using Nocturne.Tests.Shared.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// Behaviour of the cross-tenant fan-out engine <see cref="ConnectorCursorResetService"/>:
/// <list type="bullet">
///   <item>it runs only in a scope opened on the <em>target</em> tenant, and leaves the scope there;</item>
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

        var tenantAccessor = ScopeOn(_targetTenantId);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));

        var service = new ConnectorCursorResetService(
            new ConnectorConfigurationRepository(new TestNocturneDbContextFactory(db)), new TenantRepository(new TestNocturneDbContextFactory(db)), syncService.Object, tenantAccessor.Object,
            time,
            Mock.Of<ILogger<ConnectorCursorResetService>>());

        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = await service.ResetTenantCursorsAsync(
            _targetTenantId, from, [SyncDataType.Boluses], progress: null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Connectors.Should().HaveCount(2);

        // Fans out across BOTH configured connectors.
        captured.Select(c => c.ConnectorId).Should().BeEquivalentTo(["nightscout", "dexcom"]);

        // Every request forces a full re-pull (To is now), passing through the requested filters.
        captured.Should().OnlyContain(c => c.Request.To == time.GetUtcNow().UtcDateTime);
        captured.Should().OnlyContain(c => c.Request.From == from);
        captured.Should().OnlyContain(c => c.Request.DataTypes.Contains(SyncDataType.Boluses));

        // The scope was opened on the target tenant by the caller; the engine leaves it there.
        tenantAccessor.Verify(a => a.SetTenant(It.IsAny<TenantContext>()), Times.Never);
    }

    [Fact]
    public async Task ResetTenantCursors_InAScopeOnAnotherTenant_Throws_AndDoesNotSync()
    {
        var db = CreateDb();
        var syncService = new Mock<IConnectorSyncService>();
        var service = new ConnectorCursorResetService(
            new ConnectorConfigurationRepository(new TestNocturneDbContextFactory(db)), new TenantRepository(new TestNocturneDbContextFactory(db)), syncService.Object, ScopeOn(Guid.CreateVersion7()).Object,
            TimeProvider.System,
            Mock.Of<ILogger<ConnectorCursorResetService>>());

        var act = () => service.ResetTenantCursorsAsync(
            _targetTenantId, from: null, dataTypes: null, progress: null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        syncService.Verify(
            s => s.TriggerSyncAsync(It.IsAny<string>(), It.IsAny<SyncRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static Mock<ITenantAccessor> ScopeOn(Guid tenantId)
    {
        var accessor = new Mock<ITenantAccessor>();
        accessor.SetupGet(a => a.Context).Returns(new TenantContext(tenantId, "erik", "Erik", true, false));
        accessor.SetupGet(a => a.TenantId).Returns(tenantId);
        accessor.SetupGet(a => a.IsResolved).Returns(true);
        return accessor;
    }

    [Fact]
    public async Task ResetTenantCursors_UnknownTenant_ReturnsNull_AndDoesNotSync()
    {
        var db = CreateDb();
        var syncService = new Mock<IConnectorSyncService>();
        var service = new ConnectorCursorResetService(
            new ConnectorConfigurationRepository(new TestNocturneDbContextFactory(db)), new TenantRepository(new TestNocturneDbContextFactory(db)), syncService.Object, Mock.Of<ITenantAccessor>(),
            TimeProvider.System,
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
            new ConnectorConfigurationRepository(new TestNocturneDbContextFactory(db)), new TenantRepository(new TestNocturneDbContextFactory(db)), syncService.Object, ScopeOn(_targetTenantId).Object,
            TimeProvider.System,
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
