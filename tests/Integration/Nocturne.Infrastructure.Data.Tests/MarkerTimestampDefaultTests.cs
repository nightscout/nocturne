using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// The marker-declared timestamp columns against the migrated database: a write that bypasses
/// <c>SaveChanges</c> gets its stamps from the column default rather than as <c>0001-01-01</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class MarkerTimestampDefaultTests
{
    private readonly RlsCompletenessFixture _fx;

    public MarkerTimestampDefaultTests(RlsCompletenessFixture fx) => _fx = fx;

    [Fact]
    public async Task RawSqlInsert_OmittingTheStamps_GetsTheDefaults()
    {
        var id = Guid.NewGuid();

        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active)
            VALUES (@id, @slug, 'marker-timestamp-default-test', true)
            RETURNING sys_created_at, sys_updated_at, now()
            """;
        insert.Parameters.AddWithValue("@id", id);
        insert.Parameters.AddWithValue("@slug", $"ts-{id:N}");

        await using var reader = await insert.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        var transactionTime = reader.GetDateTime(2);

        reader.GetDateTime(0).Should().Be(transactionTime);
        reader.GetDateTime(1).Should().Be(transactionTime);
    }

    [Fact]
    public async Task EveryMarkerColumn_CarriesTheDefaultInTheDatabase()
    {
        var expected = MarkerColumns();
        expected.Should().HaveCountGreaterThan(90);

        var actual = new Dictionary<(string Table, string Column), string?>();
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT table_name, column_name, column_default
            FROM information_schema.columns
            WHERE table_schema = 'public'
            """;
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                actual[(reader.GetString(0), reader.GetString(1))] = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        expected
            .Where(c => actual.GetValueOrDefault(c) != "CURRENT_TIMESTAMP")
            .Select(c => $"{c.Table}.{c.Column} = {actual.GetValueOrDefault(c) ?? "<none>"}")
            .Should().BeEmpty();
    }

    private static List<(string Table, string Column)> MarkerColumns()
    {
        (Type Marker, string Property)[] markers =
        [
            (typeof(ISystemCreated), nameof(ISystemCreated.SysCreatedAt)),
            (typeof(ISystemTimestamped), nameof(ISystemTimestamped.SysUpdatedAt)),
            (typeof(IEntityCreated), nameof(IEntityCreated.CreatedAt)),
            (typeof(IEntityTimestamped), nameof(IEntityTimestamped.UpdatedAt)),
        ];

        using var context = new NocturneDbContext(
            new DbContextOptionsBuilder<NocturneDbContext>()
                .UseNpgsql("Host=localhost;Database=nocturne;Username=test;Password=test")
                .Options);

        return context.GetService<IDesignTimeModel>().Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null)
            .SelectMany(e => markers
                .Where(m => m.Marker.IsAssignableFrom(e.ClrType))
                .Select(m => (e.GetTableName()!, e.FindProperty(m.Property)!.GetColumnName())))
            .Distinct()
            .ToList();
    }
}
