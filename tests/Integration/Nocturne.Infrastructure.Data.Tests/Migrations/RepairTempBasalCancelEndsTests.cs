using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>RepairTempBasalCancelEnds</c> over temp basals with no end from every writer that leaves
/// one, in two tenants, then down and up again, on a real PostgreSQL.
/// </summary>
public class RepairTempBasalCancelEndsFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261022191833_RepairTempBasalOriginEvidence";

    internal static readonly DateTime SeededAt = new(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);
    internal static readonly DateTime Start = new(2026, 8, 1, 1, 0, 0, DateTimeKind.Utc);

    internal static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111112459");
    internal static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222222459");

    /// <summary>A v1 cancel the decomposer stored with no end.</summary>
    internal static readonly Guid DecomposedCancel = Guid.Parse("70000000-0000-7000-8000-000000002501");

    /// <summary>A v1 Temp Basal with a rate and no duration, which Nightscout also reads as a cancel.</summary>
    internal static readonly Guid DecomposedNoDuration = Guid.Parse("70000000-0000-7000-8000-000000002502");

    /// <summary>Tenant B's v1 cancel stored with no end.</summary>
    internal static readonly Guid DecomposedCancelInB = Guid.Parse("70000000-0000-7000-8000-000000002503");

    /// <summary>A v1 temp the decomposer stored with its duration.</summary>
    internal static readonly Guid DecomposedWithEnd = Guid.Parse("70000000-0000-7000-8000-000000002504");

    /// <summary>A V4 temp still running, sent with no correlation id.</summary>
    internal static readonly Guid V4Running = Guid.Parse("70000000-0000-7000-8000-000000002505");

    /// <summary>A V4 temp still running, sent with a correlation id.</summary>
    internal static readonly Guid V4RunningCorrelated = Guid.Parse("70000000-0000-7000-8000-000000002506");

    /// <summary>A MyLife temp waiting for the next record to end it.</summary>
    internal static readonly Guid MyLife = Guid.Parse("70000000-0000-7000-8000-000000002507");

    /// <summary>A Tandem temp whose next event came at the same instant.</summary>
    internal static readonly Guid Tandem = Guid.Parse("70000000-0000-7000-8000-000000002508");

    /// <summary>A V4 sync upsert still running, with a legacy id and a correlation id as well.</summary>
    internal static readonly Guid V4SyncedWithLegacyId = Guid.Parse("70000000-0000-7000-8000-000000002509");

    /// <summary>A CareLink temp, ended at the next record, with no legacy id.</summary>
    internal static readonly Guid CareLink = Guid.Parse("70000000-0000-7000-8000-000000002510");

    /// <summary>A Glooko temp with a legacy id and no correlation id, waiting for its end.</summary>
    internal static readonly Guid Glooko = Guid.Parse("70000000-0000-7000-8000-000000002511");

    /// <summary>A soft-deleted v1 cancel stored with no end.</summary>
    internal static readonly Guid DeletedDecomposedCancel = Guid.Parse("70000000-0000-7000-8000-000000002512");

    internal sealed record Row(DateTime StartTimestamp, DateTime? EndTimestamp, DateTime UpdatedAt);

    internal Dictionary<Guid, Row> Seeded { get; } = [];
    internal Dictionary<Guid, Row> AfterUp { get; } = [];
    internal Dictionary<Guid, Row> AfterSecondUp { get; } = [];

    private string _migrator = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateEmptyDatabaseAsync("repair_temp_basal_cancel_ends");
        _migrator = database.MigratorConnectionString;

        await MigrateToAsync(PriorMigration);
        await SeedAsync();
        await ReadAsync(Seeded);
        await MigrateToAsync(null);
        await ReadAsync(AfterUp);
        await MigrateToAsync(PriorMigration);
        await MigrateToAsync(null);
        await ReadAsync(AfterSecondUp);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task MigrateToAsync(string? target)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_migrator, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task SeedAsync()
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{{TenantA}}', 'tenant-a', 'Tenant A', true, now(), now()),
              ('{{TenantB}}', 'tenant-b', 'Tenant B', true, now(), now());

            SELECT set_config('app.current_tenant_id', '{{TenantA}}', false);
            INSERT INTO temp_basals
              (id, tenant_id, start_timestamp, end_timestamp, rate, origin, legacy_id, correlation_id, data_source, sync_identifier, sys_updated_at)
            VALUES
              ('{{DecomposedCancel}}', '{{TenantA}}', '{{Start:O}}', NULL, 0, 'Algorithm', '65f0c0ffee0000000000a001', '{{Guid.CreateVersion7()}}', NULL, NULL, '{{SeededAt:O}}'),
              ('{{DecomposedNoDuration}}', '{{TenantA}}', '{{Start.AddHours(1):O}}', NULL, 1.2, 'Manual', '65f0c0ffee0000000000a002', '{{Guid.CreateVersion7()}}', NULL, NULL, '{{SeededAt:O}}'),
              ('{{DecomposedWithEnd}}', '{{TenantA}}', '{{Start.AddHours(2):O}}', '{{Start.AddHours(2).AddMinutes(30):O}}', 2.0, 'Algorithm', '65f0c0ffee0000000000a003', '{{Guid.CreateVersion7()}}', NULL, NULL, '{{SeededAt:O}}'),
              ('{{V4Running}}', '{{TenantA}}', '{{Start.AddHours(3):O}}', NULL, 1.5, 'Algorithm', NULL, NULL, NULL, NULL, '{{SeededAt:O}}'),
              ('{{V4RunningCorrelated}}', '{{TenantA}}', '{{Start.AddHours(4):O}}', NULL, 1.5, 'Algorithm', NULL, '{{Guid.CreateVersion7()}}', NULL, NULL, '{{SeededAt:O}}'),
              ('{{MyLife}}', '{{TenantA}}', '{{Start.AddHours(5):O}}', NULL, 0.9, 'Scheduled', 'mylife_basal_1', NULL, 'mylife-connector', NULL, '{{SeededAt:O}}'),
              ('{{Tandem}}', '{{TenantA}}', '{{Start.AddHours(6):O}}', NULL, 0.8, 'Algorithm', 'tandem_basal_1', NULL, 'tconnectsync-connector', NULL, '{{SeededAt:O}}'),
              ('{{V4SyncedWithLegacyId}}', '{{TenantA}}', '{{Start.AddHours(7):O}}', NULL, 1.5, 'Algorithm', '65f0c0ffee0000000000a004', '{{Guid.CreateVersion7()}}', 'loop', 'loop-sync-1', '{{SeededAt:O}}'),
              ('{{CareLink}}', '{{TenantA}}', '{{Start.AddHours(8):O}}', '{{Start.AddHours(8).AddMinutes(5):O}}', 0.7, 'Algorithm', NULL, NULL, 'carelink-connector', NULL, '{{SeededAt:O}}'),
              ('{{Glooko}}', '{{TenantA}}', '{{Start.AddHours(9):O}}', NULL, 0.6, 'Manual', 'glooko_tempbasal_1', NULL, 'glooko-connector', NULL, '{{SeededAt:O}}');
            INSERT INTO temp_basals
              (id, tenant_id, start_timestamp, end_timestamp, rate, origin, legacy_id, correlation_id, deleted_at, sys_updated_at)
            VALUES ('{{DeletedDecomposedCancel}}', '{{TenantA}}', '{{Start.AddHours(10):O}}', NULL, 0, 'Algorithm', '65f0c0ffee0000000000a005', '{{Guid.CreateVersion7()}}', '{{SeededAt:O}}', '{{SeededAt:O}}');

            SELECT set_config('app.current_tenant_id', '{{TenantB}}', false);
            INSERT INTO temp_basals
              (id, tenant_id, start_timestamp, end_timestamp, rate, origin, legacy_id, correlation_id, sys_updated_at)
            VALUES ('{{DecomposedCancelInB}}', '{{TenantB}}', '{{Start:O}}', NULL, 0, 'Algorithm', '65f0c0ffee0000000000b001', '{{Guid.CreateVersion7()}}', '{{SeededAt:O}}');

            SELECT set_config('app.current_tenant_id', '', false);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ReadAsync(Dictionary<Guid, Row> into)
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        foreach (var tenant in new[] { TenantA, TenantB })
        {
            await using (var pin = conn.CreateCommand())
            {
                pin.CommandText = $"SELECT set_config('app.current_tenant_id', '{tenant}', false);";
                await pin.ExecuteNonQueryAsync();
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, start_timestamp, end_timestamp, sys_updated_at FROM temp_basals;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                into[reader.GetGuid(0)] = new Row(
                    reader.GetDateTime(1), reader.IsDBNull(2) ? null : reader.GetDateTime(2), reader.GetDateTime(3));
        }
    }
}

/// <inheritdoc cref="RepairTempBasalCancelEndsFixture"/>
[Trait("Category", "Integration")]
public class RepairTempBasalCancelEndsTests(RepairTempBasalCancelEndsFixture fixture)
    : IClassFixture<RepairTempBasalCancelEndsFixture>
{
    private Dictionary<Guid, RepairTempBasalCancelEndsFixture.Row> Rows => fixture.AfterUp;

    [Theory]
    [InlineData("70000000-0000-7000-8000-000000002501")]
    [InlineData("70000000-0000-7000-8000-000000002502")]
    [InlineData("70000000-0000-7000-8000-000000002503")]
    [InlineData("70000000-0000-7000-8000-000000002512")]
    public void A_v1_temp_basal_with_no_duration_in_every_tenant_deleted_or_not_ends_where_it_starts(string id)
    {
        var row = Rows[Guid.Parse(id)];
        row.EndTimestamp.Should().Be(row.StartTimestamp);
    }

    [Theory]
    [InlineData("70000000-0000-7000-8000-000000002504")]
    [InlineData("70000000-0000-7000-8000-000000002505")]
    [InlineData("70000000-0000-7000-8000-000000002506")]
    [InlineData("70000000-0000-7000-8000-000000002507")]
    [InlineData("70000000-0000-7000-8000-000000002508")]
    [InlineData("70000000-0000-7000-8000-000000002509")]
    [InlineData("70000000-0000-7000-8000-000000002510")]
    [InlineData("70000000-0000-7000-8000-000000002511")]
    public void A_temp_with_its_own_end_or_one_a_v4_client_or_a_connector_left_running_is_left_as_it_is(string id)
    {
        Rows[Guid.Parse(id)].Should().Be(fixture.Seeded[Guid.Parse(id)]);
    }

    [Fact]
    public void No_row_is_stamped_as_updated()
    {
        Rows.Values.Should().OnlyContain(r => r.UpdatedAt == RepairTempBasalCancelEndsFixture.SeededAt);
    }

    [Fact]
    public void Running_it_again_changes_nothing_more()
    {
        fixture.AfterSecondUp.Should().BeEquivalentTo(fixture.AfterUp);
    }
}
