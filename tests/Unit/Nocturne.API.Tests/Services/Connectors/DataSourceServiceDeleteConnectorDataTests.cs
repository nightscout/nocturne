using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// Covers the consistency fix for <see cref="DataSourceService.DeleteConnectorDataAsync"/>: every
/// data type a connector wrote must be deleted through the strongest audited path it supports (so
/// auditable types are user-attributed and the soft-delete dedup blocks re-import), and the connector
/// must be disabled so a scheduled sync can't re-import. Previously treatments hard-deleted with no
/// audit and silently re-imported on the next sync.
/// </summary>
[Trait("Category", "Unit")]
public class DataSourceServiceDeleteConnectorDataTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private const string ConnectorId = "nightscout";
    private const string AuthType = "OAuthAccessToken";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;
    private readonly string _deviceId;

    private readonly Mock<ISensorGlucoseRepository> _sensorGlucose = new();
    private readonly Mock<IMeterGlucoseRepository> _meterGlucose = new();
    private readonly Mock<ICalibrationRepository> _calibrations = new();
    private readonly Mock<IConnectorConfigurationService> _connectorConfig = new();

    private readonly AuditContext _auditContext = new()
    {
        SubjectId = Guid.Parse("00000000-0000-0000-0000-0000000000aa"),
        SubjectName = "admin@example.com",
        AuthType = AuthType,
    };

    public DataSourceServiceDeleteConnectorDataTests()
    {
        // Force-load the Nightscout connector assembly so the static metadata registry can resolve
        // the connector id to its data-source id ("nightscout-connector").
        _ = typeof(NightscoutConnectorConfiguration);
        _deviceId = ConnectorMetadataService.GetByConnectorId(ConnectorId)?.DataSourceId
            ?? throw new InvalidOperationException("Nightscout connector metadata failed to load");

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using var db = NewContext();
        db.Database.EnsureCreated();
        db.Tenants.Add(new TenantEntity { Id = TenantId, Slug = "test" });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private NocturneDbContext NewContext() => new(_dbOptions) { TenantId = TenantId };

    private DataSourceService CreateService(NocturneDbContext context) => new(
        context,
        _sensorGlucose.Object,
        _meterGlucose.Object,
        _calibrations.Object,
        _auditContext,
        _connectorConfig.Object,
        NullLogger<DataSourceService>.Instance);

    private void SeedOneOfEachType()
    {
        using var db = NewContext();

        // Auditable + soft-deletable: audited soft-delete, user-attributed, blocks re-import.
        db.Boluses.Add(new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            LegacyId = "bolus-1",
            DataSource = _deviceId,
            Timestamp = DateTime.UtcNow,
            Insulin = 1.5,
        });
        db.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            LegacyId = "carb-1",
            DataSource = _deviceId,
            Timestamp = DateTime.UtcNow,
            Carbs = 20,
        });

        // Auditable but not soft-deletable: audited hard delete.
        db.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            Source = _deviceId,
            Category = "PumpMode",
            State = "Automatic",
            StartTimestamp = DateTime.UtcNow,
        });

        // Soft-deletable but not auditable: soft-delete without an audit row.
        db.BGChecks.Add(new BGCheckEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            LegacyId = "bgcheck-1",
            DataSource = _deviceId,
            Timestamp = DateTime.UtcNow,
            Glucose = 100,
        });
        db.ApsSnapshots.Add(new ApsSnapshotEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            LegacyId = "aps-1",
            Device = _deviceId,
            Timestamp = DateTime.UtcNow,
            AidAlgorithm = "Loop",
        });

        db.SaveChanges();
    }

    [Fact]
    public async Task DeleteConnectorData_RoutesEachTypeThroughItsStrongestAuditedPath()
    {
        SeedOneOfEachType();

        await using (var ctx = NewContext())
        {
            var result = await CreateService(ctx).DeleteConnectorDataAsync(ConnectorId);
            result.Success.Should().BeTrue();
            result.DeletedCounts.Should().Contain(new KeyValuePair<string, long>("Boluses", 1));
            result.DeletedCounts.Should().Contain(new KeyValuePair<string, long>("StateSpans", 1));
        }

        await using var assertCtx = NewContext();

        // Auditable treatments are soft-deleted (row retained, DeletedAt set) — not hard-deleted.
        var bolus = await assertCtx.Boluses.IgnoreQueryFilters()
            .SingleAsync(b => b.LegacyId == "bolus-1");
        bolus.DeletedAt.Should().NotBeNull();

        // ...and carry a user-attributed delete audit row.
        (await assertCtx.MutationAuditLog.SingleAsync(a =>
            a.EntityType == "Bolus" && a.EntityId == bolus.Id && a.Action == "delete"))
            .AuthType.Should().Be(AuthType);

        // StateSpan is hard-deleted but still leaves a user-attributed delete audit row.
        (await assertCtx.StateSpans.IgnoreQueryFilters().AnyAsync(s => s.Source == _deviceId))
            .Should().BeFalse();
        (await assertCtx.MutationAuditLog.Where(a => a.EntityType == "StateSpan" && a.Action == "delete")
            .ToListAsync())
            .Should().ContainSingle().Which.AuthType.Should().Be(AuthType);

        // Non-auditable types are soft-deleted with no audit row.
        var bgCheck = await assertCtx.BGChecks.IgnoreQueryFilters()
            .SingleAsync(b => b.LegacyId == "bgcheck-1");
        bgCheck.DeletedAt.Should().NotBeNull();
        (await assertCtx.ApsSnapshots.IgnoreQueryFilters().SingleAsync(a => a.LegacyId == "aps-1"))
            .DeletedAt.Should().NotBeNull();
        (await assertCtx.MutationAuditLog.AnyAsync(a => a.EntityType == "BGCheck"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task DeleteConnectorData_BlocksReimportOfAuditableTreatments()
    {
        SeedOneOfEachType();

        await using (var ctx = NewContext())
            await CreateService(ctx).DeleteConnectorDataAsync(ConnectorId);

        // The dedup that guards bulk-create now treats the user-deleted bolus as blocking, so the
        // next sync cannot re-import it.
        await using var assertCtx = NewContext();
        var blocked = await assertCtx.GetBlockingLegacyIdsAsync<BolusEntity>(
            new HashSet<string> { "bolus-1" });
        blocked.Should().Contain("bolus-1");
    }

    [Fact]
    public async Task DeleteConnectorData_DisablesTheConnector()
    {
        SeedOneOfEachType();

        await using (var ctx = NewContext())
            await CreateService(ctx).DeleteConnectorDataAsync(ConnectorId);

        _connectorConfig.Verify(c => c.SetActiveAsync(
            ConnectorId, false, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteConnectorData_UnknownConnector_ReturnsFailureWithoutDisabling()
    {
        await using var ctx = NewContext();
        var result = await CreateService(ctx).DeleteConnectorDataAsync("not-a-connector");

        result.Success.Should().BeFalse();
        _connectorConfig.Verify(c => c.SetActiveAsync(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
