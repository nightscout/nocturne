using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Nocturne.Infrastructure.Data.Interceptors;
using Npgsql;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>FoldNotesIntoTheirRecords</c> over Notes written beside boluses in two tenants whose
/// legacy ids collide, on a real PostgreSQL, then reruns its statement as the superuser.
/// </summary>
public class FoldNotesIntoTheirRecordsFixture : IAsyncLifetime
{
    private const string MigrationUnderTest = "20261021142648_FoldNotesIntoTheirRecords";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string PriorMigration = "20261021130540_AddGoogleHealthGrantHolders";

    private string _migratorConnectionString = string.Empty;
    private string _superuserConnectionString = string.Empty;

    internal static readonly DateTime SeededAt = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111111111");
    internal static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222222222");

    internal static readonly Guid NoteApartInA = Guid.Parse("c0000000-0000-7000-8000-000000000001");
    internal static readonly Guid NoteHeldInB = Guid.Parse("c0000000-0000-7000-8000-000000000002");
    internal static readonly Guid NoteHeldOnlyInAnotherTenant = Guid.Parse("c0000000-0000-7000-8000-000000000003");
    internal static readonly Guid DeletedNoteInA = Guid.Parse("c0000000-0000-7000-8000-000000000004");

    /// <summary>Notes held by a bolus in tenant A, more than two milliseconds' worth.</summary>
    internal const int HeldInA = 2001;

    internal const int SystemTimestampGroupSize = NocturneDbContext.SystemTimestampGroupSize;

    internal Dictionary<Guid, Row> Seeded { get; } = [];
    internal Dictionary<Guid, Row> AfterUp { get; } = [];

    internal List<(Guid Note, Guid Row, string Pinned)> UpdatesAsSuperuser { get; } = [];

    internal bool? IndexValid { get; private set; }

    internal sealed record Row(Guid Tenant, DateTime UpdatedAt);

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateEmptyDatabaseAsync("fold_notes_into_records");
        _migratorConnectionString = database.MigratorConnectionString;
        _superuserConnectionString = database.SuperuserConnectionString;

        await MigrateToAsync(PriorMigration);
        await SeedAsync();
        await ReadAsync(Seeded);
        await MigrateToAsync(targetMigration: null);
        await ReadAsync(AfterUp);
        await ReadIndexAsync();
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
        return migration.UpOperations.OfType<SqlOperation>().Single(op => op.Sql.Contains("UPDATE notes")).Sql;
    }

    /// <summary>
    /// Reruns the statement as the superuser, which bypasses RLS, logging each update from a
    /// trigger, in a transaction that is rolled back.
    /// </summary>
    private async Task RunAsSuperuserAsync()
    {
        var upSql = await UpSqlAsync();

        await using var conn = new NpgsqlConnection(_superuserConnectionString);
        await conn.OpenAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        await using (var setup = conn.CreateCommand())
        {
            setup.CommandText = """
                CREATE TEMP TABLE note_updates (note uuid, row_tenant uuid, pinned text);
                CREATE FUNCTION log_note_update() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    INSERT INTO note_updates VALUES (NEW.id, NEW.tenant_id, current_setting('app.current_tenant_id', true));
                    RETURN NEW;
                END $$;
                CREATE TRIGGER log_note_update AFTER UPDATE ON notes
                    FOR EACH ROW EXECUTE FUNCTION log_note_update();
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
            read.CommandText = "SELECT note, row_tenant, pinned FROM note_updates;";
            await using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                UpdatesAsSuperuser.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2)));
        }

        await transaction.RollbackAsync();
    }

    private async Task ReadIndexAsync()
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.indisvalid FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
            WHERE c.relname = 'ix_notes_tenant_legacy_id_any';
            """;
        IndexValid = (bool?)await cmd.ExecuteScalarAsync();
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
            cmd.CommandText = "SELECT id, sys_updated_at FROM notes;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows[reader.GetGuid(0)] = new Row(tenant, reader.GetDateTime(1));
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
            INSERT INTO boluses
              (id, tenant_id, timestamp, insulin, automatic, bolus_kind, legacy_id, sys_created_at, sys_updated_at, deleted_by_user)
            SELECT gen_random_uuid(), '{{TenantA}}', '2026-05-01T00:00:00Z'::timestamptz + g * interval '1 hour',
                   1.0, false, 'Manual', 'syn-' || g, now(), '{{SeededAt:O}}', false
            FROM generate_series(1, {{HeldInA}}) g;
            INSERT INTO notes
              (id, tenant_id, timestamp, text, is_announcement, legacy_id, sys_created_at, sys_updated_at, deleted_by_user)
            SELECT gen_random_uuid(), '{{TenantA}}', '2026-05-01T00:00:00Z'::timestamptz + g * interval '1 hour',
                   'note ' || g, false, 'syn-' || g, now(), '{{SeededAt:O}}', false
            FROM generate_series(1, {{HeldInA}}) g;
            INSERT INTO notes
              (id, tenant_id, timestamp, text, is_announcement, legacy_id, sys_created_at, sys_updated_at, deleted_by_user, deleted_at)
            VALUES
              ('{{NoteApartInA}}', '{{TenantA}}', '2026-06-01T08:00:00Z', 'apart', false, 'note-only',
               now(), '{{SeededAt:O}}', false, NULL),
              ('{{DeletedNoteInA}}', '{{TenantA}}', '2026-06-01T09:00:00Z', 'gone', false, 'syn-1',
               now(), '{{SeededAt:O}}', false, '{{SeededAt:O}}');

            SELECT set_config('app.current_tenant_id', '{{TenantB}}', false);
            INSERT INTO boluses
              (id, tenant_id, timestamp, insulin, automatic, bolus_kind, legacy_id, sys_created_at, sys_updated_at, deleted_by_user)
            VALUES (gen_random_uuid(), '{{TenantB}}', '2026-07-01T08:00:00Z', 2.0, false, 'Manual', 'syn-1',
                    now(), '{{SeededAt:O}}', false);
            INSERT INTO notes
              (id, tenant_id, timestamp, text, is_announcement, legacy_id, sys_created_at, sys_updated_at, deleted_by_user)
            VALUES
              ('{{NoteHeldInB}}', '{{TenantB}}', '2026-07-01T08:00:00Z', 'held', false, 'syn-1',
               now(), '{{SeededAt:O}}', false),
              ('{{NoteHeldOnlyInAnotherTenant}}', '{{TenantB}}', '2026-07-01T09:00:00Z', 'apart', false, 'syn-2',
               now(), '{{SeededAt:O}}', false);

            SELECT set_config('app.current_tenant_id', '', false);
            """;
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <inheritdoc cref="FoldNotesIntoTheirRecordsFixture"/>
[Trait("Category", "Integration")]
public class FoldNotesIntoTheirRecordsTests(FoldNotesIntoTheirRecordsFixture fixture)
    : IClassFixture<FoldNotesIntoTheirRecordsFixture>
{
    private List<KeyValuePair<Guid, FoldNotesIntoTheirRecordsFixture.Row>> Restamped() =>
        fixture.AfterUp.Where(row => row.Value.UpdatedAt > FoldNotesIntoTheirRecordsFixture.SeededAt).ToList();

    [Fact]
    public void Every_live_note_held_by_a_record_of_its_own_tenant_is_restamped()
    {
        var restamped = Restamped();

        restamped.Count(r => r.Value.Tenant == FoldNotesIntoTheirRecordsFixture.TenantA)
            .Should().Be(FoldNotesIntoTheirRecordsFixture.HeldInA);
        restamped.Where(r => r.Value.Tenant == FoldNotesIntoTheirRecordsFixture.TenantB).Select(r => r.Key)
            .Should().Equal(FoldNotesIntoTheirRecordsFixture.NoteHeldInB);
    }

    [Theory]
    [MemberData(nameof(UntouchedNotes))]
    public void A_note_apart_deleted_or_held_only_in_another_tenant_is_untouched(Guid id)
    {
        fixture.AfterUp[id].Should().Be(fixture.Seeded[id]);
    }

    public static TheoryData<Guid> UntouchedNotes() =>
    [
        FoldNotesIntoTheirRecordsFixture.NoteApartInA,
        FoldNotesIntoTheirRecordsFixture.NoteHeldOnlyInAnotherTenant,
        FoldNotesIntoTheirRecordsFixture.DeletedNoteInA,
    ];

    [Fact]
    public void No_millisecond_holds_more_of_a_tenants_restamped_notes_than_a_save_would_stamp_in_it()
    {
        var milliseconds = Restamped()
            .GroupBy(row => (row.Value.Tenant, row.Value.UpdatedAt.Ticks / TimeSpan.TicksPerMillisecond))
            .Select(group => group.Count())
            .ToList();

        milliseconds.Should().HaveCountGreaterThan(2)
            .And.OnlyContain(count => count <= FoldNotesIntoTheirRecordsFixture.SystemTimestampGroupSize);
    }

    [Fact]
    public void The_notes_legacy_id_index_is_built_and_valid()
    {
        fixture.IndexValid.Should().BeTrue();
    }

    [Fact]
    public void Run_as_a_role_that_bypasses_rls_each_note_is_updated_once_while_its_tenant_is_pinned()
    {
        fixture.UpdatesAsSuperuser.Should().HaveCount(FoldNotesIntoTheirRecordsFixture.HeldInA + 1);
        fixture.UpdatesAsSuperuser.Select(u => u.Note).Should().OnlyHaveUniqueItems();
        fixture.UpdatesAsSuperuser.Should().OnlyContain(u => u.Pinned == u.Row.ToString());
    }
}
