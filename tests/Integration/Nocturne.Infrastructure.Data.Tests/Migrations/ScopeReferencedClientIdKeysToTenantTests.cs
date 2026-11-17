using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Npgsql;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>ScopeReferencedClientIdKeysToTenant</c> as the migrator, under FORCE ROW LEVEL SECURITY,
/// over rows that name a parent in their own tenant and rows that name one in another tenant, then
/// down and up again, on a real PostgreSQL.
/// </summary>
/// <remarks>
/// The cross-tenant rows are written as an old instance could: under the row's own tenant, past a
/// single-column foreign key that ignores row level security.
/// </remarks>
public class ScopeReferencedClientIdKeysToTenantFixture : IAsyncLifetime
{
    /// <summary>The migration immediately before the one under test.</summary>
    private const string PriorMigration = "20261027134107_ScopeClientIdKeysToTenant";

    private string _migratorConnectionString = string.Empty;

    internal static readonly Guid TenantA = Guid.Parse("a0000000-0000-7000-8000-000000000001");
    internal static readonly Guid TenantB = Guid.Parse("b0000000-0000-7000-8000-000000000001");

    internal static readonly Guid FoodA = Guid.Parse("f0000000-0000-7000-8000-0000000000a1");
    internal static readonly Guid DeletableFoodA = Guid.Parse("f0000000-0000-7000-8000-0000000000a2");
    internal static readonly Guid FoodB = Guid.Parse("f0000000-0000-7000-8000-0000000000b1");
    internal static readonly Guid SpanB = Guid.Parse("50000000-0000-7000-8000-0000000000b1");
    internal static readonly Guid SuccessorA = Guid.Parse("50000000-0000-7000-8000-0000000000a2");
    internal static readonly Guid SessionA = Guid.Parse("5e000000-0000-7000-8000-0000000000a1");
    internal static readonly Guid SessionB = Guid.Parse("5e000000-0000-7000-8000-0000000000b1");

    internal List<string> FirstRunNotices { get; } = [];

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "scope_referenced_keys");
        _migratorConnectionString = database.MigratorConnectionString;

        await SeedAsync();
        await RunAsync();
    }

    protected virtual async Task RunAsync()
    {
        await MigrateToAsync(targetMigration: null, FirstRunNotices);
        await MigrateToAsync(PriorMigration);
        await MigrateToAsync(targetMigration: null);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    protected async Task MigrateToAsync(string? targetMigration, List<string>? notices = null)
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        if (notices is not null)
            conn.Notice += (_, e) => notices.Add(e.Notice.MessageText);

        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;

        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    protected async Task<NpgsqlConnection> OpenPinnedAsync(Guid? tenantId)
    {
        var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var pin = conn.CreateCommand();
        pin.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false);";
        pin.Parameters.AddWithValue("tenant", tenantId?.ToString() ?? string.Empty);
        await pin.ExecuteNonQueryAsync();
        return conn;
    }

    internal async Task<T?> ScalarAsync<T>(Guid? tenantId, string sql)
    {
        await using var conn = await OpenPinnedAsync(tenantId);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    /// <summary>Runs <paramref name="sql"/> for a tenant and returns the error it raised, if any.</summary>
    internal async Task<PostgresException?> AttemptAsync(Guid tenantId, string sql)
    {
        await using var conn = await OpenPinnedAsync(tenantId);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        try
        {
            await cmd.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex;
        }
    }

    private async Task SeedAsync()
    {
        await using var conn = await OpenPinnedAsync(null);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{{TenantA}}', 'tenant-a', 'A', true, now(), now()),
              ('{{TenantB}}', 'tenant-b', 'B', true, now(), now());

            SELECT set_config('app.current_tenant_id', '{{TenantB}}', false);
            INSERT INTO foods (tenant_id, id, carbs, category, energy, fat, hidden, hide_after_use, name, portion, protein, subcategory)
            VALUES ('{{TenantB}}', '{{FoodB}}', 30, '', 0, 0, false, false, 'b food', 1, 0, '');
            INSERT INTO state_spans (tenant_id, id, category, state, start_timestamp)
            VALUES ('{{TenantB}}', '{{SpanB}}', 'TemporaryTarget', 'b', '2026-09-01T09:00:00Z');
            INSERT INTO sleep_sessions (tenant_id, id, created_at, detection_method, duration_ms, end_time, source, start_time, total_sleep_ms, type, updated_at)
            VALUES ('{{TenantB}}', '{{SessionB}}', now(), 'Device', 0, '2026-09-01T08:00:00Z', 'Oura', '2026-09-01T00:00:00Z', 0, 'Sleep', now());

            SELECT set_config('app.current_tenant_id', '{{TenantA}}', false);
            INSERT INTO foods (tenant_id, id, carbs, category, energy, fat, hidden, hide_after_use, name, portion, protein, subcategory)
            VALUES ('{{TenantA}}', '{{FoodA}}', 15, '', 0, 0, false, false, 'a food', 1, 0, ''),
                   ('{{TenantA}}', '{{DeletableFoodA}}', 20, '', 0, 0, false, false, 'a deletable food', 1, 0, '');
            INSERT INTO carb_intakes (tenant_id, id, carbs, timestamp)
            VALUES ('{{TenantA}}', 'ca000000-0000-7000-8000-000000000001', 50, '2026-09-01T08:00:00Z');
            INSERT INTO treatment_foods (tenant_id, id, carb_intake_id, food_id, carbs, portions) VALUES
              ('{{TenantA}}', '7f000000-0000-7000-8000-000000000001', 'ca000000-0000-7000-8000-000000000001', '{{FoodA}}', 15, 1),
              ('{{TenantA}}', '7f000000-0000-7000-8000-000000000002', 'ca000000-0000-7000-8000-000000000001', '{{FoodB}}', 30, 1),
              ('{{TenantA}}', '7f000000-0000-7000-8000-000000000003', 'ca000000-0000-7000-8000-000000000001', '{{DeletableFoodA}}', 20, 1);
            INSERT INTO connector_food_entries
              (tenant_id, id, food_id, carbs, connector_source, consumed_at, energy, external_entry_id, external_food_id, fat, meal_name, protein, servings)
            VALUES
              ('{{TenantA}}', 'cf000000-0000-7000-8000-000000000001', '{{FoodA}}', 15, 'mfp', now(), 0, 'kept', 'x', 0, '', 0, 1),
              ('{{TenantA}}', 'cf000000-0000-7000-8000-000000000002', '{{FoodB}}', 30, 'mfp', now(), 0, 'crossed', 'x', 0, '', 0, 1);
            INSERT INTO user_food_favorites (tenant_id, id, food_id, user_id) VALUES
              ('{{TenantA}}', 'ff000000-0000-7000-8000-000000000001', '{{FoodA}}', 'kept'),
              ('{{TenantA}}', 'ff000000-0000-7000-8000-000000000002', '{{FoodB}}', 'crossed');
            INSERT INTO state_spans (tenant_id, id, category, state, start_timestamp, end_timestamp, superseded_by_id) VALUES
              ('{{TenantA}}', '{{SuccessorA}}', 'TemporaryTarget', 'successor', '2026-09-01T09:00:00Z', NULL, NULL),
              ('{{TenantA}}', '50000000-0000-7000-8000-0000000000a1', 'TemporaryTarget', 'kept', '2026-09-01T08:00:00Z', '2026-09-01T09:00:00Z', '{{SuccessorA}}'),
              ('{{TenantA}}', '50000000-0000-7000-8000-0000000000a3', 'TemporaryTarget', 'crossed', '2026-09-01T08:00:00Z', '2026-09-01T09:00:00Z', '{{SpanB}}');
            INSERT INTO sleep_sessions (tenant_id, id, created_at, detection_method, duration_ms, end_time, source, start_time, total_sleep_ms, type, updated_at)
            VALUES ('{{TenantA}}', '{{SessionA}}', now(), 'Device', 0, '2026-09-01T08:00:00Z', 'Oura', '2026-09-01T00:00:00Z', 0, 'Sleep', now());
            INSERT INTO sleep_stages (tenant_id, id, sleep_session_id, start_time, end_time, ordinal, stage) VALUES
              ('{{TenantA}}', '57000000-0000-7000-8000-000000000001', '{{SessionA}}', '2026-09-01T00:00:00Z', '2026-09-01T01:00:00Z', 0, 'kept'),
              ('{{TenantA}}', '57000000-0000-7000-8000-000000000002', '{{SessionB}}', '2026-09-01T00:00:00Z', '2026-09-01T01:00:00Z', 0, 'crossed');
            INSERT INTO sleep_biometric_samples (tenant_id, id, sleep_session_id, timestamp, heart_rate) VALUES
              ('{{TenantA}}', '5b000000-0000-7000-8000-000000000001', '{{SessionA}}', '2026-09-01T00:00:00Z', 50),
              ('{{TenantA}}', '5b000000-0000-7000-8000-000000000002', '{{SessionB}}', '2026-09-01T00:00:00Z', 90);
            """;
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Runs <c>ScopeReferencedClientIdKeysToTenant</c> over a database where an earlier run of it was
/// interrupted after every key was built and the <c>foods</c> swap had committed, then migrates to
/// the end.
/// </summary>
public class InterruptedScopeReferencedClientIdKeysToTenantFixture : ScopeReferencedClientIdKeysToTenantFixture
{
    protected override async Task RunAsync()
    {
        await using (var conn = await OpenPinnedAsync(null))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE UNIQUE INDEX pk_foods_tenant_id_id ON foods (tenant_id, id);
                CREATE UNIQUE INDEX pk_state_spans_tenant_id_id ON state_spans (tenant_id, id);
                CREATE INDEX ix_treatment_foods_tenant_id_food_id ON treatment_foods (tenant_id, food_id);
                CREATE INDEX "IX_connector_food_entries_tenant_id_food_id" ON connector_food_entries (tenant_id, food_id);
                CREATE INDEX ix_user_food_favorites_tenant_id_food_id ON user_food_favorites (tenant_id, food_id);
                ALTER TABLE treatment_foods DROP CONSTRAINT "FK_treatment_foods_foods_food_id";
                ALTER TABLE connector_food_entries DROP CONSTRAINT "FK_connector_food_entries_foods_food_id";
                ALTER TABLE user_food_favorites DROP CONSTRAINT "FK_user_food_favorites_foods_food_id";
                ALTER TABLE foods
                    DROP CONSTRAINT "PK_foods",
                    ADD CONSTRAINT "PK_foods" PRIMARY KEY USING INDEX pk_foods_tenant_id_id;
                ALTER TABLE treatment_foods ADD CONSTRAINT "FK_treatment_foods_foods_tenant_id_food_id"
                    FOREIGN KEY (tenant_id, food_id) REFERENCES foods (tenant_id, id) ON DELETE SET NULL (food_id) NOT VALID;
                ALTER TABLE connector_food_entries ADD CONSTRAINT "FK_connector_food_entries_foods_tenant_id_food_id"
                    FOREIGN KEY (tenant_id, food_id) REFERENCES foods (tenant_id, id) ON DELETE SET NULL (food_id) NOT VALID;
                ALTER TABLE user_food_favorites ADD CONSTRAINT "FK_user_food_favorites_foods_tenant_id_food_id"
                    FOREIGN KEY (tenant_id, food_id) REFERENCES foods (tenant_id, id) ON DELETE CASCADE NOT VALID;
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await MigrateToAsync(targetMigration: null, FirstRunNotices);
    }
}

/// <inheritdoc cref="ScopeReferencedClientIdKeysToTenantFixture"/>
[Trait("Category", "Integration")]
public class ScopeReferencedClientIdKeysToTenantTests(ScopeReferencedClientIdKeysToTenantFixture fixture)
    : ScopeReferencedClientIdKeysToTenantTestsBase<ScopeReferencedClientIdKeysToTenantFixture>(fixture),
        IClassFixture<ScopeReferencedClientIdKeysToTenantFixture>;

/// <inheritdoc cref="InterruptedScopeReferencedClientIdKeysToTenantFixture"/>
[Trait("Category", "Integration")]
public class InterruptedScopeReferencedClientIdKeysToTenantTests(InterruptedScopeReferencedClientIdKeysToTenantFixture fixture)
    : ScopeReferencedClientIdKeysToTenantTestsBase<InterruptedScopeReferencedClientIdKeysToTenantFixture>(fixture),
        IClassFixture<InterruptedScopeReferencedClientIdKeysToTenantFixture>;

/// <summary>The end state every run of the migration reaches.</summary>
public abstract class ScopeReferencedClientIdKeysToTenantTestsBase<TFixture>(TFixture fixture)
    where TFixture : ScopeReferencedClientIdKeysToTenantFixture
{
    private static readonly Guid TenantA = ScopeReferencedClientIdKeysToTenantFixture.TenantA;
    private static readonly Guid TenantB = ScopeReferencedClientIdKeysToTenantFixture.TenantB;

    public static TheoryData<string, string, string> References => new()
    {
        { "treatment_foods", "food_id", "foods" },
        { "connector_food_entries", "food_id", "foods" },
        { "user_food_favorites", "food_id", "foods" },
        { "state_spans", "superseded_by_id", "state_spans" },
        { "sleep_stages", "sleep_session_id", "sleep_sessions" },
        { "sleep_biometric_samples", "sleep_session_id", "sleep_sessions" },
    };

    [Theory]
    [InlineData("foods")]
    [InlineData("state_spans")]
    [InlineData("sleep_sessions")]
    public async Task The_primary_key_is_the_tenant_and_the_id(string table)
    {
        var columns = await fixture.ScalarAsync<string>(null, $"""
            SELECT string_agg(a.attname, ',' ORDER BY k.ord)
            FROM pg_constraint c
            CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY AS k(attnum, ord)
            JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.attnum
            WHERE c.conrelid = '{table}'::regclass AND c.contype = 'p';
            """);

        columns.Should().Be("tenant_id,id");
    }

    [Theory]
    [InlineData("foods")]
    [InlineData("state_spans")]
    [InlineData("sleep_sessions")]
    public async Task No_second_index_on_the_tenant_and_the_id_is_left(string table)
    {
        var indexes = await fixture.ScalarAsync<string>(null, $"""
            SELECT string_agg(i.indexrelid::regclass::text, ',')
            FROM pg_index i
            WHERE i.indrelid = '{table}'::regclass
              AND i.indkey::text = (
                  SELECT string_agg(a.attnum::text, ' ' ORDER BY k.ord)
                  FROM unnest(ARRAY['tenant_id', 'id']) WITH ORDINALITY AS k(name, ord)
                  JOIN pg_attribute a ON a.attrelid = '{table}'::regclass AND a.attname = k.name);
            """);

        indexes.Should().Be($"\"PK_{table}\"");
    }

    [Theory]
    [MemberData(nameof(References))]
    public async Task Each_reference_is_one_validated_composite_foreign_key(string table, string column, string parent)
    {
        var keys = await fixture.ScalarAsync<string>(null, $"""
            SELECT string_agg(
                (SELECT string_agg(a.attname, ',' ORDER BY k.ord)
                 FROM unnest(c.conkey) WITH ORDINALITY AS k(attnum, ord)
                 JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.attnum)
                || '->' || (SELECT string_agg(a.attname, ',' ORDER BY k.ord)
                 FROM unnest(c.confkey) WITH ORDINALITY AS k(attnum, ord)
                 JOIN pg_attribute a ON a.attrelid = c.confrelid AND a.attnum = k.attnum)
                || ':' || c.convalidated, ';')
            FROM pg_constraint c
            WHERE c.conrelid = '{table}'::regclass AND c.confrelid = '{parent}'::regclass AND c.contype = 'f';
            """);

        keys.Should().Be($"tenant_id,{column}->tenant_id,id:true");
    }

    [Theory]
    [InlineData("treatment_foods", "food_id", "7f000000-0000-7000-8000-000000000001", "7f000000-0000-7000-8000-000000000002", "f0000000-0000-7000-8000-0000000000a1")]
    [InlineData("connector_food_entries", "food_id", "cf000000-0000-7000-8000-000000000001", "cf000000-0000-7000-8000-000000000002", "f0000000-0000-7000-8000-0000000000a1")]
    [InlineData("state_spans", "superseded_by_id", "50000000-0000-7000-8000-0000000000a1", "50000000-0000-7000-8000-0000000000a3", "50000000-0000-7000-8000-0000000000a2")]
    public async Task A_matching_nullable_reference_is_kept_and_a_crossed_one_is_cleared(
        string table, string column, string kept, string crossed, string parent)
    {
        var references = await fixture.ScalarAsync<string>(TenantA, $"""
            SELECT string_agg(coalesce({column}::text, 'null'), ',' ORDER BY id)
            FROM {table} WHERE id IN ('{kept}', '{crossed}');
            """);
        var keptReference = await fixture.ScalarAsync<Guid>(TenantA, $"SELECT {column} FROM {table} WHERE id = '{kept}';");

        references.Should().EndWith(",null", "a crossed reference is cleared and its row kept");
        keptReference.Should().Be(Guid.Parse(parent), "a reference to a parent in the row's own tenant survives");
    }

    [Theory]
    [InlineData("user_food_favorites", "user_id")]
    [InlineData("sleep_stages", "stage")]
    public async Task A_row_whose_required_parent_is_in_another_tenant_is_deleted(string table, string label)
    {
        var labels = await fixture.ScalarAsync<string>(TenantA, $"SELECT string_agg({label}, ',') FROM {table};");

        labels.Should().Be("kept");
    }

    [Fact]
    public async Task A_biometric_sample_whose_session_is_in_another_tenant_is_deleted()
    {
        var rates = await fixture.ScalarAsync<string>(TenantA,
            "SELECT string_agg(heart_rate::text, ',') FROM sleep_biometric_samples;");

        rates.Should().Be("50");
    }

    [Fact]
    public void The_first_run_reports_what_it_cleared_and_deleted()
    {
        fixture.FirstRunNotices.Should().Contain([
            "cleared 1 treatment_foods rows whose food_id named another tenant's row",
            "cleared 1 connector_food_entries rows whose food_id named another tenant's row",
            "deleted 1 user_food_favorites rows whose food_id named another tenant's row",
            "cleared 1 state_spans rows whose superseded_by_id named another tenant's row",
            "deleted 1 sleep_stages rows whose sleep_session_id named another tenant's row",
            "deleted 1 sleep_biometric_samples rows whose sleep_session_id named another tenant's row",
        ]);
    }

    [Fact]
    public async Task A_reference_to_another_tenants_row_is_refused()
    {
        var error = await fixture.AttemptAsync(TenantA, $"""
            UPDATE treatment_foods SET food_id = '{ScopeReferencedClientIdKeysToTenantFixture.FoodB}'
            WHERE id = '7f000000-0000-7000-8000-000000000001';
            """);

        error.Should().NotBeNull();
        error!.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Deleting_a_food_clears_the_reference_and_keeps_the_rows_tenant()
    {
        var error = await fixture.AttemptAsync(TenantA,
            $"DELETE FROM foods WHERE id = '{ScopeReferencedClientIdKeysToTenantFixture.DeletableFoodA}';");
        var row = await fixture.ScalarAsync<string>(TenantA, """
            SELECT tenant_id || ':' || coalesce(food_id::text, 'null')
            FROM treatment_foods WHERE id = '7f000000-0000-7000-8000-000000000003';
            """);

        error.Should().BeNull();
        row.Should().Be($"{TenantA}:null");
    }

    [Fact]
    public async Task The_other_tenants_parents_are_untouched()
    {
        var parents = await fixture.ScalarAsync<long>(TenantB,
            "SELECT (SELECT count(*) FROM foods) + (SELECT count(*) FROM state_spans) + (SELECT count(*) FROM sleep_sessions);");

        parents.Should().Be(3);
    }
}
