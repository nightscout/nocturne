using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Nocturne.Infrastructure.Data.Interceptors;
using Npgsql;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>ConvertMotionClassActivitiesToMotionSpans</c> over xDrip <c>motion-class</c> activities
/// stored as open Exercise spans in three tenants, beside a tenant with none, then down and up again,
/// on a real PostgreSQL.
/// </summary>
public class ConvertMotionClassActivitiesToMotionSpansFixture : IAsyncLifetime
{
    private const string MigrationUnderTest = "20261020015113_ConvertMotionClassActivitiesToMotionSpans";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string PriorMigration = "20261019000000_AddCompressionSuggestionDataSource";

    private string _migratorConnectionString = string.Empty;
    private string _superuserConnectionString = string.Empty;

    internal static readonly DateTime SeededAt = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    internal static readonly DateTime Day = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111111111");
    internal static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222222222");
    internal static readonly Guid TenantWithout = Guid.Parse("c3333333-3333-7333-8333-333333333333");

    /// <summary>
    /// Holds <see cref="BulkSpans"/> spans whose ids fall as their starts rise, so the newest open
    /// span, which only the converting statement changes, has the lowest id.
    /// </summary>
    internal static readonly Guid TenantBulk = Guid.Parse("e4444444-4444-7444-8444-444444444444");

    internal const int BulkSpans = 2001;

    internal const int SystemTimestampGroupSize = NocturneDbContext.SystemTimestampGroupSize;

    /// <summary>Stored at the epoch with no <c>timeStamp</c>, so nothing can date it.</summary>
    internal static readonly Guid Undated = Guid.Parse("d0000000-0000-7000-8000-000000000001");
    internal static readonly Guid Still = Guid.Parse("d0000000-0000-7000-8000-000000000002");
    /// <summary>Stored at the epoch, with a <c>timeStamp</c> of 09:00.</summary>
    internal static readonly Guid Walking = Guid.Parse("d0000000-0000-7000-8000-000000000003");
    internal static readonly Guid InVehicle = Guid.Parse("d0000000-0000-7000-8000-000000000004");
    internal static readonly Guid Deleted = Guid.Parse("d0000000-0000-7000-8000-000000000005");
    internal static readonly Guid Running = Guid.Parse("d0000000-0000-7000-8000-000000000006");
    internal static readonly Guid InB = Guid.Parse("d0000000-0000-7000-8000-000000000007");
    /// <summary>Two spans that start together at 11:00, after <see cref="InVehicle"/>.</summary>
    internal static readonly Guid SameStartFirst = Guid.Parse("d0000000-0000-7000-8000-000000000008");
    internal static readonly Guid SameStartSecond = Guid.Parse("d0000000-0000-7000-8000-000000000009");
    internal static readonly Guid Latest = Guid.Parse("d0000000-0000-7000-8000-00000000000a");

    internal Dictionary<Guid, Row> Seeded { get; } = [];
    internal Dictionary<Guid, Row> AfterUp { get; } = [];
    internal Dictionary<Guid, Row> AfterDown { get; } = [];
    internal Dictionary<Guid, Row> AfterSecondUp { get; } = [];

    /// <summary>
    /// Each row the migration's statements updated when run as the superuser, which bypasses RLS,
    /// with the tenant the loop had pinned at the time.
    /// </summary>
    internal List<(Guid Span, Guid Row, string Pinned)> UpdatesAsSuperuser { get; } = [];

    internal sealed record Row(
        Guid Tenant, string Category, string State, DateTime Start, DateTime? End, Guid? SupersededBy,
        string? Metadata, DateTime UpdatedAt);

    internal static long Ms(DateTime at) => new DateTimeOffset(at).ToUnixTimeMilliseconds();

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateEmptyDatabaseAsync("convert_motion_class");
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
    /// Returns one span in each tenant to its stored Exercise form and reruns the statements as the
    /// superuser, logging each update from a trigger, all in a transaction that is rolled back.
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
                UPDATE state_spans
                SET category = 'Exercise', state = 'motion-class', end_timestamp = NULL, superseded_by_id = NULL
                WHERE id IN ('{{Still}}', '{{InB}}');
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
        foreach (var tenant in new[] { TenantA, TenantB, TenantWithout, TenantBulk })
        {
            await using (var pin = conn.CreateCommand())
            {
                pin.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false);";
                pin.Parameters.AddWithValue("tenant", tenant.ToString());
                await pin.ExecuteNonQueryAsync();
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, category, state, start_timestamp, end_timestamp, superseded_by_id, metadata::text, updated_at
                FROM state_spans;
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows[reader.GetGuid(0)] = new Row(
                    tenant,
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetDateTime(3),
                    reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                    reader.IsDBNull(5) ? null : reader.GetGuid(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetDateTime(7));
        }
    }

    private static string Metadata(string motionClass, DateTime? timeStamp) =>
        timeStamp is { } at
            ? $$$"""{"createdAt": "{{{at:yyyy-MM-dd'T'HH:mm:ss'Z'}}}", "additionalProperties": {"class": "{{{motionClass}}}", "timeStamp": {{{Ms(at)}}}}}"""
            : $$$"""{"additionalProperties": {"class": "{{{motionClass}}}"}}""";

    private async Task SeedAsync()
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{{TenantA}}', 'tenant-a', 'Tenant A', true, now(), now()),
              ('{{TenantB}}', 'tenant-b', 'Tenant B', true, now(), now()),
              ('{{TenantWithout}}', 'tenant-c', 'Tenant C', true, now(), now()),
              ('{{TenantBulk}}', 'tenant-d', 'Tenant D', true, now(), now());

            SELECT set_config('app.current_tenant_id', '{{TenantA}}', false);
            INSERT INTO state_spans
              (id, tenant_id, category, state, start_timestamp, deleted_at, source, metadata,
               created_at, updated_at, deleted_by_user)
            VALUES
              ('{{Undated}}', '{{TenantA}}', 'Exercise', 'motion-class', to_timestamp(0), NULL, 'xdrip',
               '{{Metadata("on foot", null)}}', now(), '{{SeededAt:O}}', false),
              ('{{Still}}', '{{TenantA}}', 'Exercise', 'motion-class', '{{Day.AddHours(8):O}}', NULL, 'xdrip',
               '{{Metadata("still", Day.AddHours(8))}}', now(), '{{SeededAt:O}}', false),
              ('{{Walking}}', '{{TenantA}}', 'Exercise', 'motion-class', to_timestamp(0), NULL, 'xdrip',
               '{{Metadata("walking", Day.AddHours(9))}}', now(), '{{SeededAt:O}}', false),
              ('{{Deleted}}', '{{TenantA}}', 'Exercise', 'motion-class', '{{Day.AddHours(9.5):O}}', '{{SeededAt:O}}', 'xdrip',
               '{{Metadata("still", Day.AddHours(9.5))}}', now(), '{{SeededAt:O}}', false),
              ('{{InVehicle}}', '{{TenantA}}', 'Exercise', 'motion-class', '{{Day.AddHours(10):O}}', NULL, 'xdrip',
               '{{Metadata("in vehicle", Day.AddHours(10))}}', now(), '{{SeededAt:O}}', false),
              ('{{Running}}', '{{TenantA}}', 'Exercise', 'running', '{{Day.AddHours(7):O}}', NULL, 'nightscout',
               '{"notes": "parkrun"}', now(), '{{SeededAt:O}}', false),
              ('{{SameStartFirst}}', '{{TenantA}}', 'Exercise', 'motion-class', '{{Day.AddHours(11):O}}', NULL, 'xdrip',
               '{{Metadata("walking", Day.AddHours(11))}}', now(), '{{SeededAt:O}}', false),
              ('{{SameStartSecond}}', '{{TenantA}}', 'Exercise', 'motion-class', '{{Day.AddHours(11):O}}', NULL, 'nightscout',
               '{{Metadata("walking", Day.AddHours(11))}}', now(), '{{SeededAt:O}}', false),
              ('{{Latest}}', '{{TenantA}}', 'Exercise', 'motion-class', '{{Day.AddHours(12):O}}', NULL, 'xdrip',
               '{{Metadata("still", Day.AddHours(12))}}', now(), '{{SeededAt:O}}', false);

            SELECT set_config('app.current_tenant_id', '{{TenantB}}', false);
            INSERT INTO state_spans
              (id, tenant_id, category, state, start_timestamp, source, metadata,
               created_at, updated_at, deleted_by_user)
            VALUES
              ('{{InB}}', '{{TenantB}}', 'Exercise', 'motion-class', '{{Day.AddHours(8.5):O}}', 'xdrip',
               '{{Metadata("running", Day.AddHours(8.5))}}', now(), '{{SeededAt:O}}', false);

            SELECT set_config('app.current_tenant_id', '{{TenantBulk}}', false);
            INSERT INTO state_spans
              (id, tenant_id, category, state, start_timestamp, source, metadata,
               created_at, updated_at, deleted_by_user)
            SELECT ('f0000000-0000-7000-8000-' || lpad(to_hex(1000000 - g), 12, '0'))::uuid, '{{TenantBulk}}',
                   'Exercise', 'motion-class', '{{Day:O}}'::timestamptz + g * interval '1 minute', 'xdrip',
                   jsonb_build_object('additionalProperties', jsonb_build_object('class', 'still')), now(), '{{SeededAt:O}}', false
            FROM generate_series(1, {{BulkSpans}}) g;

            SELECT set_config('app.current_tenant_id', '', false);
            """;
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <inheritdoc cref="ConvertMotionClassActivitiesToMotionSpansFixture"/>
[Trait("Category", "Integration")]
public class ConvertMotionClassActivitiesToMotionSpansTests(ConvertMotionClassActivitiesToMotionSpansFixture fixture)
    : IClassFixture<ConvertMotionClassActivitiesToMotionSpansFixture>
{
    private static readonly DateTime Day = ConvertMotionClassActivitiesToMotionSpansFixture.Day;

    private static IEnumerable<KeyValuePair<Guid, ConvertMotionClassActivitiesToMotionSpansFixture.Row>> Named(Dictionary<Guid, ConvertMotionClassActivitiesToMotionSpansFixture.Row> rows) =>
        rows.Where(row => row.Value.Tenant != ConvertMotionClassActivitiesToMotionSpansFixture.TenantBulk);

    [Fact]
    public void Every_motion_class_span_in_every_tenant_becomes_a_motion_span_with_its_class_as_the_state()
    {
        var motion = Named(fixture.AfterUp)
            .Where(row => row.Value.Category == "Motion")
            .ToDictionary(row => row.Key, row => row.Value.State);

        motion.Should().BeEquivalentTo(new Dictionary<Guid, string>
        {
            [ConvertMotionClassActivitiesToMotionSpansFixture.Undated] = "on foot",
            [ConvertMotionClassActivitiesToMotionSpansFixture.Still] = "still",
            [ConvertMotionClassActivitiesToMotionSpansFixture.Walking] = "walking",
            [ConvertMotionClassActivitiesToMotionSpansFixture.Deleted] = "still",
            [ConvertMotionClassActivitiesToMotionSpansFixture.InVehicle] = "in vehicle",
            [ConvertMotionClassActivitiesToMotionSpansFixture.InB] = "running",
            [ConvertMotionClassActivitiesToMotionSpansFixture.SameStartFirst] = "walking",
            [ConvertMotionClassActivitiesToMotionSpansFixture.SameStartSecond] = "walking",
            [ConvertMotionClassActivitiesToMotionSpansFixture.Latest] = "still",
        });
        fixture.AfterUp.Values.Should().NotContain(row => row.Category == "Exercise" && row.State == "motion-class");
    }

    [Fact]
    public void A_span_stored_at_the_epoch_starts_at_its_timeStamp_and_one_without_stays()
    {
        fixture.AfterUp[ConvertMotionClassActivitiesToMotionSpansFixture.Walking].Start.Should().Be(Day.AddHours(9));
        fixture.AfterUp[ConvertMotionClassActivitiesToMotionSpansFixture.Undated].Start.Should().Be(DateTime.UnixEpoch);
    }

    [Fact]
    public void Each_live_motion_span_ends_at_the_next_start_in_its_tenant_and_the_newest_stays_open()
    {
        var after = fixture.AfterUp;
        Ends(ConvertMotionClassActivitiesToMotionSpansFixture.Undated)
            .Should().Be((Day.AddHours(8), ConvertMotionClassActivitiesToMotionSpansFixture.Still));
        Ends(ConvertMotionClassActivitiesToMotionSpansFixture.Still)
            .Should().Be((Day.AddHours(9), ConvertMotionClassActivitiesToMotionSpansFixture.Walking));
        Ends(ConvertMotionClassActivitiesToMotionSpansFixture.Walking)
            .Should().Be((Day.AddHours(10), ConvertMotionClassActivitiesToMotionSpansFixture.InVehicle));
        Ends(ConvertMotionClassActivitiesToMotionSpansFixture.InVehicle).Should().Be((Day.AddHours(11), ConvertMotionClassActivitiesToMotionSpansFixture.SameStartFirst));
        Ends(ConvertMotionClassActivitiesToMotionSpansFixture.Latest).Should().Be(((DateTime?)null, (Guid?)null));
        Ends(ConvertMotionClassActivitiesToMotionSpansFixture.InB).Should().Be(((DateTime?)null, (Guid?)null));
        Ends(ConvertMotionClassActivitiesToMotionSpansFixture.Deleted).Should().Be(((DateTime?)null, (Guid?)null));

        (DateTime? End, Guid? SupersededBy) Ends(Guid id) => (after[id].End, after[id].SupersededBy);
    }

    [Theory]
    [MemberData(nameof(SameStart))]
    public void Spans_that_start_together_both_end_at_the_next_later_start(Guid id)
    {
        var row = fixture.AfterUp[id];

        row.End.Should().Be(Day.AddHours(12));
        row.SupersededBy.Should().Be(ConvertMotionClassActivitiesToMotionSpansFixture.Latest);
    }

    public static TheoryData<Guid> SameStart() => [ConvertMotionClassActivitiesToMotionSpansFixture.SameStartFirst, ConvertMotionClassActivitiesToMotionSpansFixture.SameStartSecond];

    [Fact]
    public void No_millisecond_holds_more_of_a_tenants_changed_rows_than_a_save_would_stamp_in_it()
    {
        var bulk = fixture.AfterUp.Values.Where(row => row.Tenant == ConvertMotionClassActivitiesToMotionSpansFixture.TenantBulk).ToList();
        var milliseconds = bulk
            .GroupBy(row => row.UpdatedAt.Ticks / TimeSpan.TicksPerMillisecond)
            .Select(group => group.Count())
            .ToList();

        bulk.Should().HaveCount(ConvertMotionClassActivitiesToMotionSpansFixture.BulkSpans)
            .And.OnlyContain(row => row.Category == "Motion" && row.UpdatedAt > ConvertMotionClassActivitiesToMotionSpansFixture.SeededAt);
        bulk.Should().ContainSingle(row => row.End == null);
        milliseconds.Should().HaveCountGreaterThan(2)
            .And.OnlyContain(count => count <= ConvertMotionClassActivitiesToMotionSpansFixture.SystemTimestampGroupSize);
    }

    [Fact]
    public void A_converted_span_keeps_its_metadata_and_is_stamped_as_updated()
    {
        var converted = Named(fixture.AfterUp).Where(row => row.Value.Category == "Motion").ToList();

        converted.Should().HaveCount(9).And.OnlyContain(row =>
            row.Value.Metadata == fixture.Seeded[row.Key].Metadata
            && row.Value.UpdatedAt > ConvertMotionClassActivitiesToMotionSpansFixture.SeededAt);
    }

    [Fact]
    public void An_exercise_that_is_not_motion_class_is_untouched()
    {
        fixture.AfterUp[ConvertMotionClassActivitiesToMotionSpansFixture.Running]
            .Should().Be(fixture.Seeded[ConvertMotionClassActivitiesToMotionSpansFixture.Running]);
    }

    [Fact]
    public void Run_as_a_role_that_bypasses_rls_each_tenant_is_updated_only_while_it_is_pinned()
    {
        fixture.UpdatesAsSuperuser.Select(u => u.Span).Should().BeEquivalentTo(
        [
            ConvertMotionClassActivitiesToMotionSpansFixture.Still,
            ConvertMotionClassActivitiesToMotionSpansFixture.Still,
            ConvertMotionClassActivitiesToMotionSpansFixture.Still,
            ConvertMotionClassActivitiesToMotionSpansFixture.InB,
            ConvertMotionClassActivitiesToMotionSpansFixture.InB,
        ]);
        fixture.UpdatesAsSuperuser.Should().OnlyContain(u => u.Pinned == u.Row.ToString());
    }

    [Fact]
    public void Down_returns_each_motion_span_to_a_motion_class_exercise_and_up_again_restores_it()
    {
        foreach (var (id, row) in fixture.AfterUp.Where(row => row.Value.Category == "Motion"))
            fixture.AfterDown[id].Should().BeEquivalentTo(
                row with { Category = "Exercise", State = "motion-class" },
                options => options.Excluding(r => r.UpdatedAt));

        Unstamped(fixture.AfterSecondUp).Should().BeEquivalentTo(Unstamped(fixture.AfterUp));
    }

    private static Dictionary<Guid, ConvertMotionClassActivitiesToMotionSpansFixture.Row> Unstamped(
        Dictionary<Guid, ConvertMotionClassActivitiesToMotionSpansFixture.Row> rows) =>
        rows.ToDictionary(row => row.Key, row => row.Value with { UpdatedAt = default });
}
