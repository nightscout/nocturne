using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Connectors;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// Covers <see cref="DataSourceService.DeleteDataSourceDataAsync"/> for the entries that data-source
/// discovery surfaces from APS snapshots. Such an entry is keyed by the rig string the uploader
/// reported while the row's DataSource names the connector that imported it, so a purge that
/// required DataSource to match — or to be null before falling back to Device — matched nothing and
/// still reported success.
/// </summary>
[Trait("Category", "Unit")]
public class DataSourceServiceDeleteDataSourceTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private const string Rig = "openaps://rig";
    private const string OtherRig = "openaps://other";
    private const string ImportingConnector = "nightscout-connector";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;

    private readonly Mock<ISensorGlucoseRepository> _sensorGlucose = new();
    private readonly Mock<IMeterGlucoseRepository> _meterGlucose = new();
    private readonly Mock<ICalibrationRepository> _calibrations = new();
    private readonly Mock<IConnectorConfigurationService> _connectorConfig = new();

    private readonly AuditContext _auditContext = new()
    {
        SubjectId = Guid.Parse("00000000-0000-0000-0000-0000000000aa"),
        SubjectName = "admin@example.com",
        AuthType = "OAuthAccessToken",
    };

    public DataSourceServiceDeleteDataSourceTests()
    {
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

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private NocturneDbContext NewContext() => new(_dbOptions) { TenantId = TenantId };

    private DataSourceService CreateService(NocturneDbContext context) => new(
        context,
        _sensorGlucose.Object,
        _meterGlucose.Object,
        _calibrations.Object,
        _auditContext,
        _connectorConfig.Object,
        NullLogger<DataSourceService>.Instance);

    /// <summary>The shape DeviceStatusDecomposer writes for a connector-imported snapshot.</summary>
    private void SeedImportedSnapshot(string legacyId, string rig)
    {
        using var db = NewContext();
        db.ApsSnapshots.Add(new ApsSnapshotEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            LegacyId = legacyId,
            DataSource = ImportingConnector,
            Device = rig,
            Timestamp = DateTime.UtcNow,
            AidAlgorithm = "Loop",
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task DeleteDataSourceData_DeletesTheSnapshotsOfARigDiscoveredFromDeviceStatus()
    {
        SeedImportedSnapshot("aps-1", Rig);

        await using (var ctx = NewContext())
        {
            var result = await CreateService(ctx).DeleteDataSourceDataAsync(Rig);

            result.Success.Should().BeTrue();
            result.DeletedCounts.Should().Contain(new KeyValuePair<string, long>("DeviceStatus", 1));
        }

        await using var assert = NewContext();
        (await assert.ApsSnapshots.IgnoreQueryFilters().SingleAsync(a => a.LegacyId == "aps-1"))
            .DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteDataSourceData_LeavesAnotherRigsSnapshotsAlone()
    {
        SeedImportedSnapshot("aps-1", Rig);
        SeedImportedSnapshot("aps-2", OtherRig);

        await using (var ctx = NewContext())
            await CreateService(ctx).DeleteDataSourceDataAsync(Rig);

        await using var assert = NewContext();
        (await assert.ApsSnapshots.IgnoreQueryFilters().SingleAsync(a => a.LegacyId == "aps-2"))
            .DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task DeleteDataSourceData_UnknownSource_ReturnsNotFound()
    {
        await using var ctx = NewContext();

        var result = await CreateService(ctx).DeleteDataSourceDataAsync("no-such-source");

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(Core.Models.Services.DataSourceDeleteError.NotFound);
    }
}
