using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// Covers <see cref="ConnectorSyncCursorStore"/> — persistence of per-resource SSV2 cursors in the
/// connector's <c>sync_cursors</c> JSON column.
/// </summary>
[Trait("Category", "Unit")]
public class ConnectorSyncCursorStoreTests : IDisposable
{
    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly Guid _tenantId = Guid.CreateVersion7();

    public ConnectorSyncCursorStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite(_connection).Options;

        using var db = new NocturneDbContext(_options);
        db.Database.EnsureCreated();
        db.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = "test" });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private NocturneDbContext Db() => new(_options) { TenantId = _tenantId };

    private async Task SeedConnectorAsync(string? syncCursorsJson)
    {
        await using var db = Db();
        db.ConnectorConfigurations.Add(new ConnectorConfigurationEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            ConnectorName = "glooko",
            SyncCursorsJson = syncCursorsJson,
        });
        await db.SaveChangesAsync();
    }

    private ConnectorSyncCursorStore Store(NocturneDbContext db) =>
        new(db, NullLogger<ConnectorSyncCursorStore>.Instance);

    [Fact]
    public async Task SetThenGet_RoundTripsCursor()
    {
        await SeedConnectorAsync(null);

        await using (var db = Db())
            await Store(db).SetAsync("glooko", "/api/v2/cgm/egvs", new ConnectorSyncCursor("2026-06-03T00:00:00Z", "g3"));

        await using (var db = Db())
        {
            var cursor = await Store(db).GetAsync("glooko", "/api/v2/cgm/egvs");
            cursor.Should().Be(new ConnectorSyncCursor("2026-06-03T00:00:00Z", "g3"));
        }
    }

    [Fact]
    public async Task SetAsync_MultipleResources_CoexistInOneRow()
    {
        await SeedConnectorAsync(null);

        await using (var db = Db())
        {
            await Store(db).SetAsync("glooko", "/api/v2/cgm/egvs", new ConnectorSyncCursor("a", "1"));
            await Store(db).SetAsync("glooko", "/api/v2/pumps/normal_boluses", new ConnectorSyncCursor("b", "2"));
        }

        await using (var db = Db())
        {
            var store = Store(db);
            (await store.GetAsync("glooko", "/api/v2/cgm/egvs")).Should().Be(new ConnectorSyncCursor("a", "1"));
            (await store.GetAsync("glooko", "/api/v2/pumps/normal_boluses")).Should().Be(new ConnectorSyncCursor("b", "2"));
        }
    }

    [Fact]
    public async Task GetAsync_UnknownResource_ReturnsNull()
    {
        await SeedConnectorAsync("{\"/api/v2/cgm/egvs\":{\"lastUpdatedAt\":\"a\",\"lastGuid\":\"1\"}}");

        await using var db = Db();
        (await Store(db).GetAsync("glooko", "/api/v2/pumps/events")).Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_CorruptJson_ReturnsNullInsteadOfThrowing()
    {
        await SeedConnectorAsync("{ this is not valid json");

        await using var db = Db();
        var act = async () => await Store(db).GetAsync("glooko", "/api/v2/cgm/egvs");
        (await act.Should().NotThrowAsync()).Subject.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_WhenConnectorNotConfigured_NoOpsWithoutThrowing()
    {
        // No connector row seeded for this tenant.
        await using var db = Db();
        var act = async () =>
            await Store(db).SetAsync("glooko", "/api/v2/cgm/egvs", new ConnectorSyncCursor("a", "1"));

        await act.Should().NotThrowAsync();
        (await Store(db).GetAsync("glooko", "/api/v2/cgm/egvs")).Should().BeNull();
    }
}
