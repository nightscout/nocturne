using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// The drift audit (<see cref="DeduplicationService.AuditDuplicateFlagsAsync(CancellationToken)"/>) on
/// PostgreSQL under the runtime role and Row Level Security. Drift is written by raw SQL, as no writer
/// may leave any.
/// </summary>
[Trait("Category", "Integration")]
public class DuplicateFlagAuditTests(DuplicateFlagAuditTests.Database database)
    : IClassFixture<DuplicateFlagAuditTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;
        public ServiceProvider Provider { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("duplicate_flag_audit");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
            Provider = TenantReaderData.BuildProvider(AppConnectionString);
        }

        public async Task DisposeAsync() => await Provider.DisposeAsync();
    }

    private static readonly string[] Tables =
    [
        "sensor_glucose", "boluses", "carb_intakes", "bg_checks", "device_events", "notes",
        "bolus_calculations", "temp_basals", "state_spans",
    ];

    [Fact]
    public async Task An_audit_repairs_drift_both_ways_in_its_own_tenant_only_and_a_second_finds_none()
    {
        var audited = await SeedTenantAsync();
        var other = await SeedTenantAsync();
        foreach (var tenant in new[] { audited, other })
            await SeedDriftAsync(tenant);
        (await MismatchesAsync(other)).Should().HaveCount(6);

        var drift = await AuditAsync(audited);

        drift.Should().BeEquivalentTo(new[]
        {
            new DuplicateFlagDrift(RecordType.SensorGlucose, FlaggedNotDuplicate: 1, DuplicateNotFlagged: 1, Repaired: 2),
            new DuplicateFlagDrift(RecordType.Bolus, FlaggedNotDuplicate: 2, DuplicateNotFlagged: 1, Repaired: 3),
            new DuplicateFlagDrift(RecordType.StateSpan, FlaggedNotDuplicate: 0, DuplicateNotFlagged: 1, Repaired: 1),
        });
        (await MismatchesAsync(audited)).Should().BeEmpty();
        (await MismatchesAsync(other)).Should().HaveCount(6, "the audit reads and writes its own tenant only");
        (await AuditAsync(audited)).Should().BeEmpty();
    }

    /// <summary>
    /// One save stamps up to a thousand rows with one <c>sys_updated_at</c>, so page boundaries fall
    /// inside a run of equal stamps and the walk continues on the id.
    /// </summary>
    [Fact]
    public async Task Drift_spread_over_many_pages_is_all_repaired()
    {
        var tenant = await SeedTenantAsync();
        var glucose = Enumerable.Range(0, 25).Select(i => Glucose(tenant, DateTime.UtcNow.AddMinutes(-5 * i))).ToList();
        var spans = Enumerable.Range(0, 7).Select(i => Span(tenant, DateTime.UtcNow.AddHours(-i))).ToList();
        await using (var db = Context(tenant))
        {
            db.AddRange(glucose);
            db.AddRange(spans);
            await db.SaveChangesAsync();
        }

        var flagged = glucose.Where((_, i) => i % 2 == 0).Select(g => g.Id).Concat(spans.Where((_, i) => i % 2 == 1).Select(s => s.Id));
        await ExecuteAsync(tenant, $"UPDATE sensor_glucose SET is_duplicate = true WHERE id = ANY ('{{{string.Join(',', flagged)}}}'::uuid[])");
        await ExecuteAsync(tenant, $"UPDATE state_spans SET is_duplicate = true WHERE id = ANY ('{{{string.Join(',', flagged)}}}'::uuid[])");

        await using var scope = Scope(tenant);
        var drift = await Dedup(scope).AuditDuplicateFlagsAsync(pageSize: 4, default);

        drift.Should().BeEquivalentTo(new[]
        {
            new DuplicateFlagDrift(RecordType.SensorGlucose, 13, 0, 13),
            new DuplicateFlagDrift(RecordType.StateSpan, 3, 0, 3),
        });
        (await MismatchesAsync(tenant)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_repair_writes_only_rows_that_still_disagree_when_it_runs()
    {
        var tenant = await SeedTenantAsync();
        var (primary, copy) = await CarbGroupAsync(tenant);
        var unlinked = Carb(tenant, DateTime.UtcNow.AddHours(-3));
        await using (var db = Context(tenant))
        {
            db.CarbIntakes.Add(unlinked);
            await db.SaveChangesAsync();
        }

        await ExecuteAsync(tenant, $"UPDATE carb_intakes SET is_duplicate = true WHERE id = '{unlinked.Id}'");

        int repaired;
        await using (var db = Context(tenant))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            repaired = await DuplicateGroupPrimaries.RepairFlagsAsync(
                db, RecordType.CarbIntake, [primary.Id, copy.Id, unlinked.Id], default);
            await transaction.CommitAsync();
        }

        repaired.Should().Be(1, "the group's two rows already agree with their links");
        (await FlaggedAsync(tenant, "carb_intakes")).Should().Equal(copy.Id);
    }

    /// <summary>
    /// A row the audit read as drift is put right by a primary move still committing. The repair waits
    /// on the move's links and then derives from what the move committed; without the lock it would
    /// write from links the move has already replaced, and leave the row wrong once the move commits.
    /// </summary>
    [Fact]
    public async Task A_repair_waits_for_a_primary_move_holding_the_links_and_derives_from_what_it_committed()
    {
        var tenant = await SeedTenantAsync();
        var (primary, copy) = await CarbGroupAsync(tenant);
        await ExecuteAsync(tenant, $"UPDATE carb_intakes SET is_duplicate = false WHERE id = '{copy.Id}'");

        await using var move = Context(tenant);
        await using var transaction = await move.Database.BeginTransactionAsync();
        await DuplicateGroupPrimaries.SetPrimariesAsync(
            move, RecordType.CarbIntake, [new(copy.Id, true), new(primary.Id, false)], default);

        var audit = Task.Run(() => AuditAsync(tenant));
        // Blocked means the audit has read the drift and waits on the copy's link the move holds: a
        // fixed delay let a slow start read the drift only after the commit, and find none.
        await PostgresLockWaits.UntilBlockedAsync(database.AppConnectionString, audit);

        await transaction.CommitAsync();
        var drift = await audit.WaitAsync(TimeSpan.FromSeconds(30));

        drift.Should().BeEquivalentTo(new[] { new DuplicateFlagDrift(RecordType.CarbIntake, 0, 1, 0) });
        (await FlaggedAsync(tenant, "carb_intakes")).Should().Equal(primary.Id);
        (await MismatchesAsync(tenant)).Should().BeEmpty();
    }

    /// <summary>
    /// Drift in both directions on three tables, soft-deleted rows included, and one clean table:
    /// a glucose copy unflagged and a lone reading flagged; a bolus copy unflagged, its primary flagged
    /// and a lone soft-deleted bolus flagged; a state span copy unflagged; notes untouched.
    /// </summary>
    private async Task SeedDriftAsync(Guid tenant)
    {
        var at = DateTime.UtcNow.AddHours(-2);
        var glucose = new[] { Glucose(tenant, at), Glucose(tenant, at), Glucose(tenant, at.AddMinutes(5)) };
        var boluses = new[] { Bolus(tenant, at), Bolus(tenant, at), Bolus(tenant, at.AddHours(-1)) };
        boluses[2].DeletedAt = DateTime.UtcNow;
        var spans = new[] { Span(tenant, at), Span(tenant, at) };
        var note = new NoteEntity { Id = Guid.CreateVersion7(), TenantId = tenant, Timestamp = at, Text = "clean" };

        await using (var db = Context(tenant))
        {
            db.AddRange(glucose);
            db.AddRange(boluses);
            db.AddRange(spans);
            db.Add(note);
            await db.SaveChangesAsync();
            await DuplicateGroups.LinkAsync(db, RecordType.SensorGlucose, glucose[0].Id, glucose[1].Id);
            await DuplicateGroups.LinkAsync(db, RecordType.Bolus, boluses[0].Id, boluses[1].Id);
            await DuplicateGroups.LinkAsync(db, RecordType.StateSpan, spans[0].Id, spans[1].Id);
        }

        await ExecuteAsync(tenant, $"""
            UPDATE sensor_glucose SET is_duplicate = NOT is_duplicate WHERE id IN ('{glucose[1].Id}', '{glucose[2].Id}');
            UPDATE boluses SET is_duplicate = NOT is_duplicate WHERE id IN ('{boluses[0].Id}', '{boluses[1].Id}', '{boluses[2].Id}');
            UPDATE state_spans SET is_duplicate = false WHERE id = '{spans[1].Id}';
            """);
    }

    private async Task<(CarbIntakeEntity Primary, CarbIntakeEntity Copy)> CarbGroupAsync(Guid tenant)
    {
        var at = DateTime.UtcNow.AddHours(-1);
        var primary = Carb(tenant, at);
        var copy = Carb(tenant, at.AddSeconds(10));
        await using var db = Context(tenant);
        db.CarbIntakes.AddRange(primary, copy);
        await db.SaveChangesAsync();
        await DuplicateGroups.LinkAsync(db, RecordType.CarbIntake, primary.Id, copy.Id);
        return (primary, copy);
    }

    private static SensorGlucoseEntity Glucose(Guid tenant, DateTime at) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = tenant, Timestamp = at, DataSource = "test", Mgdl = 120,
    };

    private static BolusEntity Bolus(Guid tenant, DateTime at) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = tenant, Timestamp = at, DataSource = "test", Insulin = 2,
    };

    private static CarbIntakeEntity Carb(Guid tenant, DateTime at) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = tenant, Timestamp = at, DataSource = "test", Carbs = 30,
    };

    private static StateSpanEntity Span(Guid tenant, DateTime start) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = tenant, Category = "PumpMode", State = "Automatic",
        StartTimestamp = start, Source = "test",
    };

    private async Task<IReadOnlyList<DuplicateFlagDrift>> AuditAsync(Guid tenant)
    {
        await using var scope = Scope(tenant);
        return await scope.ServiceProvider.GetRequiredService<IDeduplicationService>().AuditDuplicateFlagsAsync();
    }

    private AsyncServiceScope Scope(Guid tenant)
    {
        var scope = database.Provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, tenant);
        return scope;
    }

    private static DeduplicationService Dedup(AsyncServiceScope scope) =>
        (DeduplicationService)scope.ServiceProvider.GetRequiredService<IDeduplicationService>();

    private NocturneDbContext Context(Guid tenant) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.AppConnectionString)
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options) { TenantId = tenant };

    /// <summary>Every row, soft-deleted included, whose flag disagrees with the anti-join.</summary>
    private Task<List<string>> MismatchesAsync(Guid tenant) => QueryAsync(tenant, string.Join("\nUNION ALL\n",
        Tables.Select(table => $"""
            SELECT '{table}:' || r.id::text FROM {table} r
            WHERE r.is_duplicate IS DISTINCT FROM EXISTS (
                SELECT 1 FROM linked_records lr
                WHERE lr.tenant_id = r.tenant_id AND lr.record_id = r.id AND NOT lr.is_primary)
            """)));

    private async Task<List<Guid>> FlaggedAsync(Guid tenant, string table) =>
        (await QueryAsync(tenant, $"SELECT id::text FROM {table} WHERE is_duplicate ORDER BY id")).Select(Guid.Parse).ToList();

    private async Task<List<string>> QueryAsync(Guid tenant, string sql)
    {
        await using var conn = new NpgsqlConnection(database.AppConnectionString);
        await conn.OpenAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        await SetTenantAsync(conn, tenant);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }

    private async Task ExecuteAsync(Guid tenant, string sql)
    {
        await using var conn = new NpgsqlConnection(database.AppConnectionString);
        await conn.OpenAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        await SetTenantAsync(conn, tenant);
        await using (var cmd = new NpgsqlCommand(sql, conn))
            await cmd.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task SetTenantAsync(NpgsqlConnection conn, Guid tenant)
    {
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.current_tenant_id', @t, true)", conn);
        cmd.Parameters.AddWithValue("t", tenant.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Guid> SeedTenantAsync()
    {
        var tenant = Guid.CreateVersion7();
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@id, @slug, 'duplicate-flag-audit', true, now(), now())
            """, conn);
        cmd.Parameters.AddWithValue("id", tenant);
        cmd.Parameters.AddWithValue("slug", $"dup-audit-{tenant:N}");
        await cmd.ExecuteNonQueryAsync();
        return tenant;
    }
}
