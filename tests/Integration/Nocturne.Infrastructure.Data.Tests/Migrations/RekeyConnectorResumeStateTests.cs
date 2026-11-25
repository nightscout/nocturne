using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Core.Models.Connectors;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>RekeyConnectorResumeState</c> on a real PostgreSQL as the migrator over three tenants:
/// Glooko, Tandem and MyFitnessPal rows in the old shape, a row already in the new shape, values
/// that do not read, an empty row, a row whose low-water marks are not an object, and rows of
/// connectors the migration does not touch (one of them no longer registered). Glooko's stamps are
/// seeded as its cursor writer stored them, and Tandem's zone in use as the release before this one
/// wrote it. The runtime role then reads the moved state through the repository.
/// </summary>
public class RekeyConnectorResumeStateFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261123213408_AddTeamStaff";

    internal static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111113572");
    internal static readonly Guid TenantB = Guid.Parse("a2222222-2222-7222-8222-222222223572");
    internal static readonly Guid TenantC = Guid.Parse("a3333333-3333-7333-8333-333333333572");

    internal const string Ssv2Cursor = """{"LastUpdatedAt":"2026-11-19T00:00:00.000Z","LastGuid":"g1"}""";

    /// <summary>A Tandem zone in use as the release before this one stored it.</summary>
    internal const string TandemZone = """{"id":"America/New_York","source":"tenantZone"}""";

    /// <summary>When tenant A's last Glooko walk completed, as its stamp was seeded.</summary>
    internal static readonly DateTimeOffset GlookoWalkedAt = new(2026, 11, 20, 3, 4, 5, 678, TimeSpan.Zero);

    private string _migrator = string.Empty;
    private string _app = string.Empty;

    internal Dictionary<(Guid Tenant, string Connector), StoredRow> RowsBefore { get; private set; } = [];
    internal Dictionary<(Guid Tenant, string Connector), StoredRow> RowsAfter { get; private set; } = [];
    internal Dictionary<(Guid Tenant, string Connector), StoredRow> RowsAfterSecondRun { get; private set; } = [];
    internal IReadOnlyDictionary<string, DateTime> GlookoResumeAsRead { get; private set; } = new Dictionary<string, DateTime>();
    internal IReadOnlyDictionary<string, DateTime> GlookoResumeOfTenantCAsRead { get; private set; } = new Dictionary<string, DateTime>();
    internal JsonNode? GlookoSsv2AsRead { get; private set; }
    internal JsonNode? TandemProfileAsRead { get; private set; }
    internal JsonNode? TandemZoneAsRead { get; private set; }

    /// <summary>A row's state columns, each as PostgreSQL's canonical jsonb text.</summary>
    internal sealed record StoredRow(string? Vendor, string? Resume, string Secrets, string Configuration);

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "rekey_connector_state");
        _migrator = database.MigratorConnectionString;
        _app = database.AppConnectionString;

        foreach (var tenant in new[] { TenantA, TenantB, TenantC })
            await NpgsqlTestCommands.ExecuteAsync(_migrator, $"""
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES ('{tenant}', 'rekey-{tenant:N}', 'rekey', true, now(), now());
                """);

        await InsertAsync(TenantA, "glooko",
            vendor: $$"""
                {"full-walk":{"LastUpdatedAt":"{{OldWriterStamp(GlookoWalkedAt)}}","LastGuid":null},
                 "full-walk-attempt":{"LastUpdatedAt":"{{OldWriterStamp(new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero))}}","LastGuid":null},
                 "lookback-read":{"LastUpdatedAt":"not a time"},
                 "/api/v2/cgm/egvs":{{Ssv2Cursor}}}
                """,
            resume: """{"Glucose":"2026-01-01T00:00:00Z"}""");
        await InsertAsync(TenantA, "tandem",
            vendor: $$"""
                {"profile:1":{"LastUpdatedAt":null,"LastGuid":"fp1"},
                 "profile:2":"fp2",
                 "profile:3":{"LastGuid":5},
                 "profile:4":7,
                 "other":{"LastGuid":"kept"},
                 "timeZone":{{TandemZone}}}
                """);
        await InsertAsync(TenantA, "myfitnesspal",
            secrets: """{"password":"sealed","lastFullWalkAt":"sealed-stamp"}""",
            configuration: """{"username":"u","lastFullWalkAt":"2026-11-01T00:00:00Z"}""");
        await InsertAsync(TenantA, "dexcom",
            vendor: """{"full-walk":{"LastUpdatedAt":"2026-11-20T03:04:05Z"},"profile:1":{"LastGuid":"x"}}""");
        await InsertAsync(TenantA, "retiredconnector",
            vendor: """{"profile:1":{"LastGuid":"x"},"full-walk":{"LastUpdatedAt":"2026-11-20T03:04:05Z"}}""",
            secrets: """{"lastFullWalkAt":"sealed"}""");

        await InsertAsync(TenantB, "glooko",
            vendor: $$"""{"/api/v2/cgm/egvs":{{Ssv2Cursor}}}""",
            resume: """{"full-walk":"2026-11-20T03:04:05.678000Z"}""");
        await InsertAsync(TenantB, "tandem");
        await InsertAsync(TenantB, "myfitnesspal", secrets: """{"password":"sealed"}""");
        await InsertAsync(TenantB, "nightscout", vendor: "\"not an object\"");

        await InsertAsync(TenantC, "glooko",
            vendor: """
                {"full-walk":{"LastUpdatedAt":"2026-11-20T03:04:05.0000000+00:00","LastGuid":null},
                 "full-walk-attempt":{"LastUpdatedAt":"infinity","LastGuid":null},
                 "health":{"LastGuid":"h"}}
                """,
            resume: "[1]");
        await InsertAsync(TenantC, "tandem", vendor: """{"timeZone":{"id":"UTC+10:00","source":"connectorOffset"}}""");

        RowsBefore = await ReadRowsAsync();

        await using (var context = new NocturneDbContext(Options(_migrator)))
            await context.GetService<IMigrator>().MigrateAsync();

        RowsAfter = await ReadRowsAsync();
        await NpgsqlTestCommands.ExecuteAsync(_migrator, RekeyConnectorResumeState.RekeySql);
        RowsAfterSecondRun = await ReadRowsAsync();

        await using (var db = new NocturneDbContext(Options(_app)) { TenantId = TenantA })
        {
            var configurations = new ConnectorConfigurationRepository(db.AsFactory());
            GlookoResumeAsRead = await configurations.GetResumeStateAsync("glooko", CancellationToken.None);
            GlookoSsv2AsRead = await configurations.GetVendorStateAsync("glooko", "/api/v2/cgm/egvs", CancellationToken.None);
            TandemProfileAsRead = await configurations.GetVendorStateAsync("tandem", "profile:1", CancellationToken.None);
            TandemZoneAsRead = await configurations.GetVendorStateAsync(
                "tandem", ConnectorTimeZone.VendorStateKey, CancellationToken.None);
        }

        await using (var db = new NocturneDbContext(Options(_app)) { TenantId = TenantC })
            GlookoResumeOfTenantCAsRead = await new ConnectorConfigurationRepository(db.AsFactory())
                .GetResumeStateAsync("glooko", CancellationToken.None);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>PostgreSQL's canonical text of a jsonb value, for comparing with a stored column.</summary>
    internal async Task<string> CanonicalAsync(string json)
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT @json::jsonb::text", conn);
        cmd.Parameters.AddWithValue("json", json);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private Task InsertAsync(
        Guid tenant, string connector, string? vendor = null, string? resume = null,
        string secrets = "{}", string configuration = "{}") =>
        NpgsqlTestCommands.ExecuteAsync(_migrator, $"""
            SELECT set_config('app.current_tenant_id', '{tenant}', false);
            INSERT INTO connector_configurations
                (id, tenant_id, connector_name, configuration, secrets, schema_version, last_modified,
                 sync_cursors, backfill_low_water_marks)
            VALUES ('{Guid.CreateVersion7()}', '{tenant}', '{connector}', {Literal(configuration)}, {Literal(secrets)}, 1, now(),
                    {Literal(vendor)}, {Literal(resume)});
            """);

    /// <summary>The stamp Glooko's cursor writer stored: a round-trip time with its offset.</summary>
    private static string OldWriterStamp(DateTimeOffset at) =>
        at.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static string Literal(string? json) =>
        json is null ? "NULL" : $"'{json.Replace("'", "''")}'::jsonb";

    private DbContextOptions<NocturneDbContext> Options(string connectionString) =>
        TestContexts.MigrationsOptions(connectionString);

    private async Task<Dictionary<(Guid, string), StoredRow>> ReadRowsAsync()
    {
        var rows = new Dictionary<(Guid, string), StoredRow>();
        foreach (var tenant in new[] { TenantA, TenantB, TenantC })
        {
            await using var conn = new NpgsqlConnection(_migrator);
            await conn.OpenAsync();
            await using (var pin = new NpgsqlCommand($"SELECT set_config('app.current_tenant_id', '{tenant}', false)", conn))
                await pin.ExecuteNonQueryAsync();
            await using var cmd = new NpgsqlCommand("""
                SELECT connector_name, sync_cursors::text, backfill_low_water_marks::text, secrets::text, configuration::text
                FROM connector_configurations
                """, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows[(tenant, reader.GetString(0))] = new StoredRow(
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4));
        }

        return rows;
    }
}

[Trait("Category", "Integration")]
public class RekeyConnectorResumeStateTests(RekeyConnectorResumeStateFixture fixture)
    : IClassFixture<RekeyConnectorResumeStateFixture>
{
    private static readonly Guid A = RekeyConnectorResumeStateFixture.TenantA;
    private static readonly Guid B = RekeyConnectorResumeStateFixture.TenantB;
    private static readonly Guid C = RekeyConnectorResumeStateFixture.TenantC;

    private RekeyConnectorResumeStateFixture.StoredRow After(Guid tenant, string connector) =>
        fixture.RowsAfter[(tenant, connector)];

    [Fact]
    public async Task Glooko_stamps_move_to_the_resume_slot_beside_its_marks_and_an_unreadable_one_is_dropped()
    {
        var row = After(A, "glooko");

        row.Vendor.Should().Be(await fixture.CanonicalAsync(
            $$"""{"/api/v2/cgm/egvs":{{RekeyConnectorResumeStateFixture.Ssv2Cursor}}}"""));
        row.Resume.Should().Be(await fixture.CanonicalAsync("""
            {"Glucose":"2026-01-01T00:00:00Z",
             "full-walk":"2026-11-20T03:04:05.678000Z",
             "full-walk-attempt":"2026-11-20T03:00:00.000000Z"}
            """));
    }

    [Fact]
    public async Task Tandem_fingerprints_become_bare_strings_and_one_without_a_string_is_dropped()
    {
        After(A, "tandem").Vendor.Should().Be(await fixture.CanonicalAsync(
            $$"""{"profile:1":"fp1","profile:2":"fp2","other":{"LastGuid":"kept"},"timeZone":{{RekeyConnectorResumeStateFixture.TandemZone}}}"""));
        After(A, "tandem").Resume.Should().BeNull();
    }

    [Fact]
    public async Task MyFitnessPal_loses_its_walk_stamp_from_secrets_and_configuration()
    {
        var row = After(A, "myfitnesspal");

        row.Secrets.Should().Be(await fixture.CanonicalAsync("""{"password":"sealed"}"""));
        row.Configuration.Should().Be(await fixture.CanonicalAsync("""{"username":"u"}"""));
    }

    [Fact]
    public void Rows_of_other_connectors_and_rows_already_in_the_new_shape_are_unchanged()
    {
        foreach (var key in new[]
                 {
                     (A, "dexcom"), (A, "retiredconnector"), (B, "glooko"), (B, "tandem"),
                     (B, "myfitnesspal"), (B, "nightscout"), (C, "tandem"),
                 })
            fixture.RowsAfter[key].Should().Be(fixture.RowsBefore[key], $"{key} is not rekeyed");
    }

    [Fact]
    public async Task Glooko_marks_that_are_not_an_object_are_replaced_by_its_stamps_and_an_infinite_one_is_dropped()
    {
        var row = After(C, "glooko");

        row.Resume.Should().Be(await fixture.CanonicalAsync("""{"full-walk":"2026-11-20T03:04:05.000000Z"}"""));
        row.Vendor.Should().Be(await fixture.CanonicalAsync("""{"health":{"LastGuid":"h"}}"""));
        fixture.GlookoResumeOfTenantCAsRead.Should().BeEquivalentTo(new Dictionary<string, DateTime>
        {
            ["full-walk"] = new(2026, 11, 20, 3, 4, 5, DateTimeKind.Utc),
        });
    }

    [Fact]
    public void The_moved_walk_stamp_keeps_Glooko_off_a_full_walk_until_its_interval_has_passed()
    {
        var walked = fixture.GlookoResumeAsRead[GlookoConstants.FullWalkStamp];
        var walkedAt = RekeyConnectorResumeStateFixture.GlookoWalkedAt;

        FullWalkSchedule.IsDue(walked, GlookoConstants.FullWalkInterval, walkedAt.AddMinutes(30)).Should().BeFalse();
        FullWalkSchedule.IsDue(walked, GlookoConstants.FullWalkInterval, walkedAt + GlookoConstants.FullWalkInterval)
            .Should().BeTrue();
    }

    [Fact]
    public void Every_row_is_still_there() =>
        fixture.RowsAfter.Keys.Should().BeEquivalentTo(fixture.RowsBefore.Keys);

    [Fact]
    public void A_second_run_changes_nothing() =>
        fixture.RowsAfterSecondRun.Should().BeEquivalentTo(fixture.RowsAfter);

    [Fact]
    public void The_runtime_role_reads_the_moved_state_through_the_repository()
    {
        fixture.GlookoResumeAsRead.Should().BeEquivalentTo(new Dictionary<string, DateTime>
        {
            ["Glucose"] = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ["full-walk"] = new DateTime(2026, 11, 20, 3, 4, 5, 678, DateTimeKind.Utc),
            ["full-walk-attempt"] = new(2026, 11, 20, 3, 0, 0, DateTimeKind.Utc),
        });
        fixture.GlookoResumeAsRead.Values.Should().OnlyContain(at => at.Kind == DateTimeKind.Utc);
        fixture.GlookoSsv2AsRead!["LastGuid"]!.GetValue<string>().Should().Be("g1");
        fixture.TandemProfileAsRead!.GetValue<string>().Should().Be("fp1");
        ConnectorTimeZone.FromStored(fixture.TandemZoneAsRead).Should().BeEquivalentTo(new ConnectorTimeZone
        {
            Id = "America/New_York", Source = ConnectorTimeZoneSource.TenantZone,
        });
    }
}
