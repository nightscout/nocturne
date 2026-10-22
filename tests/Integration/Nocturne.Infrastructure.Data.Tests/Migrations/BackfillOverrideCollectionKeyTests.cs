using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Nocturne.Infrastructure.Data.Interceptors;
using Npgsql;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>BackfillOverrideCollectionKey</c> over state spans stored the way the decomposers wrote
/// them before Override metadata carried a collection key, in two tenants, then down and up again,
/// on a real PostgreSQL.
/// </summary>
public class BackfillOverrideCollectionKeyFixture : IAsyncLifetime
{
    private const string MigrationUnderTest = "20261018230558_BackfillOverrideCollectionKey";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string PriorMigration = "20261018055216_DeduplicateApsSnapshotCurves";

    private string _migratorConnectionString = string.Empty;
    private string _superuserConnectionString = string.Empty;

    /// <summary>A time before the migration ran, stamped on every seeded row.</summary>
    internal static readonly DateTime SeededAt = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111111111");
    internal static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222222222");

    internal static readonly Guid TreatmentInA = Guid.Parse("c0000000-0000-7000-8000-000000000001");
    internal static readonly Guid TreatmentInB = Guid.Parse("c0000000-0000-7000-8000-000000000002");
    internal static readonly Guid DeviceStatus = Guid.Parse("c0000000-0000-7000-8000-000000000003");
    internal static readonly Guid Keyed = Guid.Parse("c0000000-0000-7000-8000-000000000004");
    internal static readonly Guid WithoutOriginalId = Guid.Parse("c0000000-0000-7000-8000-000000000005");
    internal static readonly Guid TemporaryTarget = Guid.Parse("c0000000-0000-7000-8000-000000000006");

    /// <summary>
    /// Keyless treatment overrides seeded in tenant A beside <see cref="TreatmentInA"/>, enough to
    /// fill more than two milliseconds of <c>NocturneDbContext.SystemTimestampGroupSize</c>.
    /// </summary>
    internal const int BulkOverrides = 2001;

    internal const int SystemTimestampGroupSize = NocturneDbContext.SystemTimestampGroupSize;

    internal Dictionary<Guid, Row> Seeded { get; } = [];
    internal Dictionary<Guid, Row> AfterUp { get; } = [];
    internal Dictionary<Guid, Row> AfterDown { get; } = [];
    internal Dictionary<Guid, Row> AfterSecondUp { get; } = [];

    /// <summary>
    /// Each row the migration's statement updated when run as the superuser, which bypasses RLS,
    /// with the tenant the loop had pinned at the time.
    /// </summary>
    internal List<(Guid Span, Guid Row, string Pinned)> UpdatesAsSuperuser { get; } = [];

    internal sealed record Row(Guid Tenant, string? Metadata, DateTime UpdatedAt);

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateEmptyDatabaseAsync("backfill_override_collection");
        _migratorConnectionString = database.MigratorConnectionString;
        _superuserConnectionString = database.SuperuserConnectionString;

        await MigrateToAsync(PriorMigration);
        await SeedAsync();
        await ReadAsync(Seeded);
        await MigrateToAsync(targetMigration: null);
        await ReadAsync(AfterUp);
        await MigrateToAsync(PriorMigration);
        await ReadAsync(AfterDown);
        await MigrateToAsync(targetMigration: null);
        await ReadAsync(AfterSecondUp);
        await RunAsSuperuserAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task MigrateToAsync(string? targetMigration)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;

        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private async Task<string> UpSqlAsync()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsql => npgsql.UseNocturneMigrations())
            .Options;

        await using var context = new NocturneDbContext(options);
        var assembly = context.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(
            assembly.Migrations[MigrationUnderTest], context.Database.ProviderName!);
        return migration.UpOperations.OfType<SqlOperation>().Single().Sql;
    }

    /// <summary>
    /// Strips the key from both treatment spans again and reruns the statement as the superuser,
    /// logging each update from a trigger, all in a transaction that is rolled back.
    /// </summary>
    private async Task RunAsSuperuserAsync()
    {
        var upSql = await UpSqlAsync();

        await using var conn = new NpgsqlConnection(_superuserConnectionString);
        await conn.OpenAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        await using (var setup = conn.CreateCommand())
        {
            setup.CommandText = $$"""
                CREATE TEMP TABLE span_updates (span uuid, row_tenant uuid, pinned text);
                CREATE FUNCTION log_span_update() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    INSERT INTO span_updates VALUES (NEW.id, NEW.tenant_id, current_setting('app.current_tenant_id', true));
                    RETURN NEW;
                END $$;
                UPDATE state_spans SET metadata = metadata - 'collection'
                WHERE id IN ('{{TreatmentInA}}', '{{TreatmentInB}}');
                CREATE TRIGGER log_span_update AFTER UPDATE ON state_spans
                    FOR EACH ROW EXECUTE FUNCTION log_span_update();
                """;
            await setup.ExecuteNonQueryAsync();
        }

        await using (var up = conn.CreateCommand())
        {
            up.CommandText = upSql;
            await up.ExecuteNonQueryAsync();
        }

        await using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT span, row_tenant, pinned FROM span_updates;";
            await using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                UpdatesAsSuperuser.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2)));
        }

        await transaction.RollbackAsync();
    }

    private async Task ReadAsync(Dictionary<Guid, Row> rows)
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        foreach (var tenant in new[] { TenantA, TenantB })
        {
            await using (var pin = conn.CreateCommand())
            {
                pin.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false);";
                pin.Parameters.AddWithValue("tenant", tenant.ToString());
                await pin.ExecuteNonQueryAsync();
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, metadata::text, updated_at FROM state_spans;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows[reader.GetGuid(0)] = new Row(tenant, reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetDateTime(2));
        }
    }

    private async Task SeedAsync()
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{{TenantA}}', 'tenant-a', 'Tenant A', true, now(), now()),
              ('{{TenantB}}', 'tenant-b', 'Tenant B', true, now(), now());

            SELECT set_config('app.current_tenant_id', '{{TenantA}}', false);
            INSERT INTO state_spans
              (id, tenant_id, category, state, start_timestamp, source, original_id, metadata,
               created_at, updated_at, deleted_by_user)
            VALUES
              ('{{TreatmentInA}}', '{{TenantA}}', 'Override', 'Custom', '2026-06-01T08:00:00Z', 'Loop', 'ns-1',
               '{"reason": "Running", "insulinNeedsScaleFactor": 0.8, "enteredBy": "Loop", "utcOffset": 0}',
               now(), '{{SeededAt:O}}', false),
              ('{{DeviceStatus}}', '{{TenantA}}', 'Override', 'Custom', '2026-06-01T08:05:00Z', 'loop://iPhone', 'ns-2',
               '{"name": "Running", "multiplier": 0.8}',
               now(), '{{SeededAt:O}}', false),
              ('{{WithoutOriginalId}}', '{{TenantA}}', 'Override', 'Custom', '2026-06-01T09:00:00Z', 'glooko', NULL,
               '{"utcOffset": 0}',
               now(), '{{SeededAt:O}}', false);
            INSERT INTO state_spans
              (id, tenant_id, category, state, start_timestamp, source, original_id, metadata,
               created_at, updated_at, deleted_by_user)
            SELECT gen_random_uuid(), '{{TenantA}}', 'Override', 'Custom',
                   '2026-05-01T00:00:00Z'::timestamptz + g * interval '1 hour', 'Loop', 'bulk-' || g,
                   '{"utcOffset": 0}', now(), '{{SeededAt:O}}', false
            FROM generate_series(1, {{BulkOverrides}}) g;

            SELECT set_config('app.current_tenant_id', '{{TenantB}}', false);
            INSERT INTO state_spans
              (id, tenant_id, category, state, start_timestamp, source, original_id, metadata,
               created_at, updated_at, deleted_by_user)
            VALUES
              ('{{TreatmentInB}}', '{{TenantB}}', 'Override', 'Custom', '2026-07-01T08:00:00Z', 'Loop', 'ns-3',
               '{"reason": "Sick", "utcOffset": 600}',
               now(), '{{SeededAt:O}}', false),
              ('{{Keyed}}', '{{TenantB}}', 'Override', 'Custom', '2026-07-01T09:00:00Z', 'loop://iPhone', 'ns-4',
               '{"collection": "devicestatus", "utcOffset": 0}',
               now(), '{{SeededAt:O}}', false),
              ('{{TemporaryTarget}}', '{{TenantB}}', 'TemporaryTarget', 'Active', '2026-07-01T10:00:00Z', 'Loop', 'ns-5',
               '{"targetTop": 150, "utcOffset": 0}',
               now(), '{{SeededAt:O}}', false);

            SELECT set_config('app.current_tenant_id', '', false);
            """;
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <inheritdoc cref="BackfillOverrideCollectionKeyFixture"/>
[Trait("Category", "Integration")]
public class BackfillOverrideCollectionKeyTests(BackfillOverrideCollectionKeyFixture fixture)
    : IClassFixture<BackfillOverrideCollectionKeyFixture>
{
    private static JsonObject Json(string? text) => JsonNode.Parse(text!)!.AsObject();

    [Theory]
    [MemberData(nameof(TreatmentSpans))]
    public void A_treatment_override_in_every_tenant_gains_the_treatments_key_and_keeps_its_metadata(Guid id)
    {
        var before = Json(fixture.Seeded[id].Metadata);
        var after = Json(fixture.AfterUp[id].Metadata);

        after["collection"]!.GetValue<string>().Should().Be("treatments");
        after.Remove("collection");
        JsonNode.DeepEquals(after, before).Should().BeTrue();
    }

    public static TheoryData<Guid> TreatmentSpans() =>
        [BackfillOverrideCollectionKeyFixture.TreatmentInA, BackfillOverrideCollectionKeyFixture.TreatmentInB];

    [Theory]
    [MemberData(nameof(TreatmentSpans))]
    public void A_treatment_override_is_stamped_as_updated_so_v3_history_delivers_it_again(Guid id)
    {
        fixture.Seeded[id].UpdatedAt.Should().Be(BackfillOverrideCollectionKeyFixture.SeededAt);
        fixture.AfterUp[id].UpdatedAt.Should().BeAfter(BackfillOverrideCollectionKeyFixture.SeededAt);
    }

    [Fact]
    public void No_millisecond_holds_more_of_a_tenants_backfilled_rows_than_a_save_would_stamp_in_it()
    {
        var backfilled = fixture.AfterUp
            .Where(row => row.Value.UpdatedAt > BackfillOverrideCollectionKeyFixture.SeededAt)
            .ToList();
        var milliseconds = backfilled
            .GroupBy(row => (row.Value.Tenant, row.Value.UpdatedAt.Ticks / TimeSpan.TicksPerMillisecond))
            .Select(group => group.Count())
            .ToList();

        backfilled.Should().HaveCount(BackfillOverrideCollectionKeyFixture.BulkOverrides + 2);
        milliseconds.Should().HaveCountGreaterThan(2)
            .And.OnlyContain(count => count <= BackfillOverrideCollectionKeyFixture.SystemTimestampGroupSize);
    }

    [Theory]
    [MemberData(nameof(UntouchedSpans))]
    public void Spans_that_are_not_keyless_treatment_overrides_are_untouched(Guid id)
    {
        fixture.AfterUp[id].Should().Be(fixture.Seeded[id]);
    }

    public static TheoryData<Guid> UntouchedSpans() =>
    [
        BackfillOverrideCollectionKeyFixture.DeviceStatus,
        BackfillOverrideCollectionKeyFixture.Keyed,
        BackfillOverrideCollectionKeyFixture.WithoutOriginalId,
        BackfillOverrideCollectionKeyFixture.TemporaryTarget,
    ];

    [Fact]
    public void Run_as_a_role_that_bypasses_rls_each_tenant_is_updated_only_while_it_is_pinned()
    {
        fixture.UpdatesAsSuperuser.Select(u => u.Span).Should().BeEquivalentTo(
            [BackfillOverrideCollectionKeyFixture.TreatmentInA, BackfillOverrideCollectionKeyFixture.TreatmentInB]);
        fixture.UpdatesAsSuperuser.Should().OnlyContain(u => u.Pinned == u.Row.ToString());
    }

    [Fact]
    public void Down_leaves_the_key_and_up_again_changes_nothing()
    {
        fixture.AfterDown.Should().BeEquivalentTo(fixture.AfterUp);
        fixture.AfterSecondUp.Should().BeEquivalentTo(fixture.AfterUp);
    }
}
