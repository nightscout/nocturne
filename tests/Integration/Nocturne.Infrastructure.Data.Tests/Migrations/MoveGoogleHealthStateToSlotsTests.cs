using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Models.Health;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>MoveGoogleHealthStateToSlots</c> on a real PostgreSQL as the migrator over one Google
/// Health row per tenant in every shape the release before it wrote: a finished import with a
/// data scan, a half-finished one with a retry window and a queued import, a disconnect in
/// progress beside a scheduled run's progress and a first window that failed, a purged position,
/// values that do not read, times without an offset or with one other than UTC, a finished import
/// whose cursor is above its floor, a row already in the new shape, an empty row, the backfill keys
/// the connector kept in its configuration before that, and another connector's row carrying the
/// same key names. The migrator's session runs in a time zone other than UTC. The runtime role then
/// reads the moved state; the migration is then taken down, which gives back what the release before
/// reads, and up again.
/// </summary>
public class MoveGoogleHealthStateToSlotsFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261124082620_RekeyConnectorResumeState";
    private const string GoogleHealth = "googlehealth";
    internal const string SessionTimeZone = "Australia/Brisbane";

    internal static readonly Guid Finished = Tenant(1);
    internal static readonly Guid HalfFinished = Tenant(2);
    internal static readonly Guid Disconnecting = Tenant(3);
    internal static readonly Guid Unreadable = Tenant(4);
    internal static readonly Guid PartlyUnreadable = Tenant(5);
    internal static readonly Guid NewShape = Tenant(6);
    internal static readonly Guid Empty = Tenant(7);
    internal static readonly Guid Purged = Tenant(8);
    internal static readonly Guid Zoned = Tenant(9);
    internal static readonly Guid FinishedAboveFloor = Tenant(0);

    internal static readonly Guid[] Tenants =
        [Finished, HalfFinished, Disconnecting, Unreadable, PartlyUnreadable, NewShape, Empty, Purged, Zoned, FinishedAboveFloor];

    internal static readonly Guid DisconnectRequest = Guid.Parse("d1111111-1111-7111-8111-111111113572");
    internal static readonly Guid DisconnectSubject = Guid.Parse("d2222222-2222-7222-8222-222222223572");

    internal const string Inventory =
        """[{"dataType":"steps","count":12,"errorCode":null},{"dataType":"sleep","count":0,"errorCode":"permission_denied"}]""";

    internal const string FinishedState =
        $$"""{"lastSyncedTo":"2026-11-24T10:05:37.1230000Z","backfillCursorDate":"2026-08-25T00:00:00.0000000Z","backfillFloorDate":"2026-08-25T00:00:00.0000000Z","backfillComplete":true,"inventory":{{Inventory}}}""";

    internal const string HalfFinishedState =
        """{"lastSyncedTo":"2026-11-24T09:00:00.0000000Z","backfillCursorDate":"2026-07-01T00:00:00.0000000Z","backfillFloorDate":"2026-01-01T00:00:00.0000000Z","backfillComplete":false,"backfillChunkDays":7}""";

    internal const string FirstWindowFailedState =
        """{"backfillCursorDate":null,"backfillFloorDate":"2000-01-01T00:00:00.0000000Z","backfillComplete":false,"backfillChunkDays":4}""";

    internal const string ZonedState =
        """{"lastSyncedTo":"2026-11-24T10:00:00","backfillCursorDate":"2026-08-25T00:00:00+10:00","backfillFloorDate":"2026-01-01T00:00:00.0000000Z","backfillComplete":false}""";

    internal const string FinishedAboveFloorState =
        """{"lastSyncedTo":"2026-11-24T08:30:00.0000000Z","backfillCursorDate":"2026-06-01T00:00:00.0000000Z","backfillFloorDate":"2026-03-01T00:00:00.0000000Z","backfillComplete":true}""";

    internal const string QueuedProgress =
        """{"Phase":"queued","DataType":null,"CompletedDataTypes":0,"TotalDataTypes":4,"PagesRead":0,"WorkerOwned":true}""";

    internal const string ScheduledProgress =
        """{"Phase":"reading","DataType":"heart-rate","CompletedDataTypes":1,"TotalDataTypes":3,"PagesRead":12,"WorkerOwned":false}""";

    internal static readonly string Disconnect =
        $$"""{"RequestId":"{{DisconnectRequest}}","SubjectId":"{{DisconnectSubject}}"}""";

    private string _migrator = string.Empty;
    private string _app = string.Empty;

    internal Dictionary<(Guid Tenant, string Connector), StoredRow> RowsBefore { get; private set; } = [];
    internal Dictionary<(Guid Tenant, string Connector), StoredRow> RowsAfter { get; private set; } = [];
    internal Dictionary<(Guid Tenant, string Connector), StoredRow> RowsAfterSecondRun { get; private set; } = [];
    internal Dictionary<(Guid Tenant, string Connector), StoredRow> RowsAfterDown { get; private set; } = [];
    internal Dictionary<(Guid Tenant, string Connector), StoredRow> RowsAfterUpAgain { get; private set; } = [];
    internal Dictionary<Guid, StoredWork> WorkAfter { get; private set; } = [];
    internal Dictionary<Guid, StoredWork> WorkAfterSecondRun { get; private set; } = [];
    internal Dictionary<Guid, StoredWork> WorkAfterUpAgain { get; private set; } = [];
    internal Dictionary<Guid, IReadOnlyDictionary<string, DateTime>> ResumeAsRead { get; } = [];
    internal Dictionary<Guid, JsonNode?> ChunkDaysAsRead { get; } = [];
    internal Dictionary<Guid, GoogleHealthWork> WorkAsRead { get; } = [];
    internal bool WorkTableExistsAfterDown { get; private set; }
    internal (string Tenant, string TimeZone) SettingsAfterMove { get; private set; }
    internal (string Tenant, string TimeZone) SettingsAfterRestore { get; private set; }

    /// <summary>A row's state columns, each as PostgreSQL's canonical jsonb text.</summary>
    internal sealed record StoredRow(string? Vendor, string? Resume, string Configuration);

    /// <summary>A <c>google_health_work</c> row as stored.</summary>
    internal sealed record StoredWork(
        string? Phase, string? DataType, int Completed, int Total, int Pages, bool WorkerOwned,
        Guid? RequestId, Guid? SubjectId);

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "move_google_health_state");
        _migrator = new NpgsqlConnectionStringBuilder(database.MigratorConnectionString) { Timezone = SessionTimeZone }
            .ConnectionString;
        _app = database.AppConnectionString;

        foreach (var tenant in Tenants)
            await NpgsqlTestCommands.ExecuteAsync(_migrator, $"""
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES ('{tenant}', 'gh-{tenant:N}', 'gh', true, now(), now());
                """);

        await InsertAsync(Finished, GoogleHealth,
            vendor: Vendor(health: FinishedState),
            configuration: """
                {"importFrom":null,"lookbackDays":90,"syncSteps":true,
                 "lastSyncedTo":"2026-01-01T00:00:00Z","backfillCursorDate":"2025-12-01","backfillFloorDate":"2025-01-01",
                 "backfillComplete":false,"backfillChunkDays":3}
                """);
        await InsertAsync(Finished, "dexcom",
            vendor: Vendor(health: FinishedState, progress: QueuedProgress, disconnect: Disconnect),
            configuration: """{"lastSyncedTo":"2026-01-01T00:00:00Z"}""");
        await InsertAsync(HalfFinished, GoogleHealth,
            vendor: Vendor(health: HalfFinishedState, progress: QueuedProgress, extra: "\"kept\":{\"a\":1}"));
        await InsertAsync(Disconnecting, GoogleHealth,
            vendor: Vendor(health: FirstWindowFailedState, progress: ScheduledProgress, disconnect: Disconnect));
        await InsertAsync(Unreadable, GoogleHealth,
            vendor: """
                {"health":{"LastUpdatedAt":null,"LastGuid":"not json"},
                 "googleHealthProgress":{"Phase":"paused","TotalDataTypes":1},
                 "googleHealthDisconnect":{"RequestId":"nope","SubjectId":"nope"}}
                """);
        await InsertAsync(PartlyUnreadable, GoogleHealth,
            vendor: Vendor(
                health: """{"lastSyncedTo":"not a time","backfillCursorDate":"2026-05-01T00:00:00Z","backfillFloorDate":42,"backfillComplete":true,"backfillChunkDays":"seven","inventory":{"not":"an array"}}""",
                progress: """{"Phase":"reading","CompletedDataTypes":1.5,"TotalDataTypes":2,"PagesRead":0,"WorkerOwned":false}""",
                disconnect: Disconnect));
        await InsertAsync(NewShape, GoogleHealth,
            vendor: """{"backfillChunkDays":2,"inventory":[]}""",
            resume: """{"lastSyncedTo":"2026-11-24T08:00:00.000000Z"}""");
        await InsertAsync(Empty, GoogleHealth);
        await InsertAsync(Purged, GoogleHealth, vendor: """{"health":{"LastUpdatedAt":null,"LastGuid":null}}""");
        await InsertAsync(Zoned, GoogleHealth, vendor: Vendor(health: ZonedState));
        await InsertAsync(FinishedAboveFloor, GoogleHealth, vendor: Vendor(health: FinishedAboveFloorState));

        RowsBefore = await ReadRowsAsync();

        await using (var context = new NocturneDbContext(Options(_migrator)))
            await context.GetService<IMigrator>().MigrateAsync();

        RowsAfter = await ReadRowsAsync();
        WorkAfter = await ReadWorkAsync();
        await NpgsqlTestCommands.ExecuteAsync(_migrator, MoveGoogleHealthStateToSlots.MoveSql);
        RowsAfterSecondRun = await ReadRowsAsync();
        WorkAfterSecondRun = await ReadWorkAsync();

        foreach (var tenant in new[] { Finished, HalfFinished, Disconnecting, PartlyUnreadable })
        {
            await using var db = new NocturneDbContext(Options(_app)) { TenantId = tenant };
            var configurations = new ConnectorConfigurationRepository(db.AsFactory());
            ResumeAsRead[tenant] = await configurations.GetResumeStateAsync(GoogleHealth, CancellationToken.None);
            ChunkDaysAsRead[tenant] = await configurations.GetVendorStateAsync(
                GoogleHealth, GoogleHealthState.BackfillChunkDays, CancellationToken.None);
            WorkAsRead[tenant] = await new GoogleHealthWorkRepository(db.AsFactory()).GetAsync(CancellationToken.None);
        }

        await using (var context = new NocturneDbContext(Options(_migrator)))
            await context.GetService<IMigrator>().MigrateAsync(PriorMigration);
        RowsAfterDown = await ReadRowsAsync();
        WorkTableExistsAfterDown = await ScalarAsync<bool>("SELECT to_regclass('google_health_work') IS NOT NULL");

        await using (var context = new NocturneDbContext(Options(_migrator)))
            await context.GetService<IMigrator>().MigrateAsync();
        RowsAfterUpAgain = await ReadRowsAsync();
        WorkAfterUpAgain = await ReadWorkAsync();

        SettingsAfterMove = await SettingsAfterAsync(MoveGoogleHealthStateToSlots.MoveSql);
        SettingsAfterRestore = await SettingsAfterAsync(MoveGoogleHealthStateToSlots.RestoreSql);
    }

    /// <summary>
    /// The tenant GUC and the time zone a later statement of the same transaction sees after
    /// <paramref name="sql"/>, which is rolled back.
    /// </summary>
    private async Task<(string Tenant, string TimeZone)> SettingsAfterAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        await using (var run = new NpgsqlCommand(sql, conn, transaction))
            await run.ExecuteNonQueryAsync();
        await using var read = new NpgsqlCommand(
            "SELECT coalesce(current_setting('app.current_tenant_id', true), ''), current_setting('TimeZone')", conn, transaction);
        await using var reader = await read.ExecuteReaderAsync();
        await reader.ReadAsync();
        var settings = (reader.GetString(0), reader.GetString(1));
        await reader.CloseAsync();
        await transaction.RollbackAsync();
        return settings;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>PostgreSQL's canonical text of a jsonb value, for comparing with a stored column.</summary>
    internal Task<string> CanonicalAsync(string json) => ScalarAsync<string>("SELECT @json::jsonb::text", json);

    /// <summary>A <c>sync_cursors</c> in the shape the release before wrote.</summary>
    internal static string Vendor(string? health = null, string? progress = null, string? disconnect = null, string? extra = null)
    {
        var keys = new List<string>();
        if (health is not null)
            keys.Add($"\"health\":{new JsonObject { ["LastUpdatedAt"] = null, ["LastGuid"] = health }.ToJsonString()}");
        if (progress is not null)
            keys.Add($"\"googleHealthProgress\":{progress}");
        if (disconnect is not null)
            keys.Add($"\"googleHealthDisconnect\":{disconnect}");
        if (extra is not null)
            keys.Add(extra);
        return "{" + string.Join(",", keys) + "}";
    }

    private Task InsertAsync(
        Guid tenant, string connector, string? vendor = null, string? resume = null, string configuration = "{}") =>
        NpgsqlTestCommands.ExecuteAsync(_migrator, $"""
            SELECT set_config('app.current_tenant_id', '{tenant}', false);
            INSERT INTO connector_configurations
                (id, tenant_id, connector_name, configuration, secrets, schema_version, last_modified,
                 sync_cursors, backfill_low_water_marks)
            VALUES ('{Guid.CreateVersion7()}', '{tenant}', '{connector}', {Literal(configuration)}, {Literal("{}")}, 1, now(),
                    {Literal(vendor)}, {Literal(resume)});
            """);

    private static string Literal(string? json) =>
        json is null ? "NULL" : $"'{json.Replace("'", "''")}'::jsonb";

    private static Guid Tenant(int n) => Guid.Parse($"b{n}{n}{n}{n}{n}{n}{n}-{n}{n}{n}{n}-7{n}{n}{n}-8{n}{n}{n}-{n}{n}{n}{n}{n}{n}{n}{n}3572");

    private DbContextOptions<NocturneDbContext> Options(string connectionString) =>
        TestContexts.MigrationsOptions(connectionString);

    private async Task<Dictionary<(Guid, string), StoredRow>> ReadRowsAsync()
    {
        var rows = new Dictionary<(Guid, string), StoredRow>();
        foreach (var tenant in Tenants)
        {
            await using var conn = await PinnedAsync(tenant);
            await using var cmd = new NpgsqlCommand(
                "SELECT connector_name, sync_cursors::text, backfill_low_water_marks::text, configuration::text FROM connector_configurations",
                conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows[(tenant, reader.GetString(0))] = new StoredRow(
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3));
        }

        return rows;
    }

    private async Task<Dictionary<Guid, StoredWork>> ReadWorkAsync()
    {
        var rows = new Dictionary<Guid, StoredWork>();
        foreach (var tenant in Tenants)
        {
            await using var conn = await PinnedAsync(tenant);
            await using var cmd = new NpgsqlCommand("""
                SELECT tenant_id, progress_phase, progress_data_type, progress_completed_data_types,
                       progress_total_data_types, progress_pages_read, progress_worker_owned,
                       disconnect_request_id, disconnect_subject_id
                FROM google_health_work
                """, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows[reader.GetGuid(0)] = new StoredWork(
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetBoolean(6),
                    reader.IsDBNull(7) ? null : reader.GetGuid(7),
                    reader.IsDBNull(8) ? null : reader.GetGuid(8));
        }

        return rows;
    }

    private async Task<NpgsqlConnection> PinnedAsync(Guid tenant)
    {
        var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var pin = new NpgsqlCommand($"SELECT set_config('app.current_tenant_id', '{tenant}', false)", conn);
        await pin.ExecuteNonQueryAsync();
        return conn;
    }

    private async Task<T> ScalarAsync<T>(string sql, string? json = null)
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (json is not null)
            cmd.Parameters.AddWithValue("json", json);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}

[Trait("Category", "Integration")]
public class MoveGoogleHealthStateToSlotsTests(MoveGoogleHealthStateToSlotsFixture fixture)
    : IClassFixture<MoveGoogleHealthStateToSlotsFixture>
{
    private const string GoogleHealth = "googlehealth";
    private static readonly Guid Finished = MoveGoogleHealthStateToSlotsFixture.Finished;
    private static readonly Guid HalfFinished = MoveGoogleHealthStateToSlotsFixture.HalfFinished;
    private static readonly Guid Disconnecting = MoveGoogleHealthStateToSlotsFixture.Disconnecting;
    private static readonly Guid Unreadable = MoveGoogleHealthStateToSlotsFixture.Unreadable;
    private static readonly Guid PartlyUnreadable = MoveGoogleHealthStateToSlotsFixture.PartlyUnreadable;

    private MoveGoogleHealthStateToSlotsFixture.StoredRow After(Guid tenant) => fixture.RowsAfter[(tenant, GoogleHealth)];

    [Fact]
    public async Task A_finished_import_moves_its_times_to_resume_and_its_scan_to_vendor()
    {
        After(Finished).Resume.Should().Be(await fixture.CanonicalAsync("""
            {"lastSyncedTo":"2026-11-24T10:05:37.123000Z",
             "backfillCursorDate":"2026-08-25T00:00:00.000000Z",
             "backfillFloorDate":"2026-08-25T00:00:00.000000Z"}
            """));
        After(Finished).Vendor.Should().Be(await fixture.CanonicalAsync(
            $$"""{"inventory":{{MoveGoogleHealthStateToSlotsFixture.Inventory}}}"""));
        fixture.WorkAfter.Should().NotContainKey(Finished);
    }

    [Fact]
    public async Task The_backfill_keys_left_in_the_configuration_are_removed_and_the_rest_kept() =>
        After(Finished).Configuration.Should().Be(await fixture.CanonicalAsync(
            """{"importFrom":null,"lookbackDays":90,"syncSteps":true}"""));

    [Fact]
    public async Task A_half_finished_import_keeps_its_range_and_retry_window_and_its_queued_import()
    {
        After(HalfFinished).Resume.Should().Be(await fixture.CanonicalAsync("""
            {"lastSyncedTo":"2026-11-24T09:00:00.000000Z",
             "backfillCursorDate":"2026-07-01T00:00:00.000000Z",
             "backfillFloorDate":"2026-01-01T00:00:00.000000Z"}
            """));
        After(HalfFinished).Vendor.Should().Be(await fixture.CanonicalAsync("""{"backfillChunkDays":7,"kept":{"a":1}}"""));
        fixture.WorkAfter[HalfFinished].Should().Be(new MoveGoogleHealthStateToSlotsFixture.StoredWork(
            "queued", null, 0, 4, 0, true, null, null));
    }

    [Fact]
    public async Task A_disconnect_in_progress_and_a_scheduled_runs_progress_move_to_the_work_table()
    {
        fixture.WorkAfter[Disconnecting].Should().Be(new MoveGoogleHealthStateToSlotsFixture.StoredWork(
            "reading", "heart-rate", 1, 3, 12, false,
            MoveGoogleHealthStateToSlotsFixture.DisconnectRequest, MoveGoogleHealthStateToSlotsFixture.DisconnectSubject));
        After(Disconnecting).Vendor.Should().Be(await fixture.CanonicalAsync("""{"backfillChunkDays":4}"""));
        After(Disconnecting).Resume.Should().Be(await fixture.CanonicalAsync(
            """{"backfillFloorDate":"2000-01-01T00:00:00.000000Z"}"""));
    }

    [Fact]
    public async Task Values_that_do_not_read_are_dropped_and_the_row_kept()
    {
        After(Unreadable).Vendor.Should().Be(await fixture.CanonicalAsync("{}"));
        After(Unreadable).Resume.Should().BeNull();
        fixture.WorkAfter.Should().NotContainKey(Unreadable);

        After(PartlyUnreadable).Vendor.Should().Be(await fixture.CanonicalAsync("{}"));
        fixture.WorkAfter[PartlyUnreadable].Should().Be(new MoveGoogleHealthStateToSlotsFixture.StoredWork(
            null, null, 0, 0, 0, false,
            MoveGoogleHealthStateToSlotsFixture.DisconnectRequest, MoveGoogleHealthStateToSlotsFixture.DisconnectSubject));
    }

    [Fact]
    public async Task A_finished_import_whose_floor_does_not_read_stays_finished_at_its_cursor()
    {
        After(PartlyUnreadable).Resume.Should().Be(await fixture.CanonicalAsync("""
            {"backfillCursorDate":"2026-05-01T00:00:00.000000Z",
             "backfillFloorDate":"2026-05-01T00:00:00.000000Z"}
            """));
        var resume = fixture.ResumeAsRead[PartlyUnreadable];
        GoogleHealthState.BackfillComplete(resume[GoogleHealthState.BackfillCursor], resume[GoogleHealthState.BackfillFloor])
            .Should().BeTrue();
    }

    [Fact]
    public async Task A_finished_import_whose_cursor_is_above_a_readable_floor_stays_finished_at_its_cursor() =>
        After(MoveGoogleHealthStateToSlotsFixture.FinishedAboveFloor).Resume.Should().Be(await fixture.CanonicalAsync("""
            {"lastSyncedTo":"2026-11-24T08:30:00.000000Z",
             "backfillCursorDate":"2026-06-01T00:00:00.000000Z",
             "backfillFloorDate":"2026-06-01T00:00:00.000000Z"}
            """));

    [Fact]
    public async Task A_time_without_an_offset_reads_as_utc_and_one_with_an_offset_moves_to_utc_in_any_session_zone() =>
        After(MoveGoogleHealthStateToSlotsFixture.Zoned).Resume.Should().Be(await fixture.CanonicalAsync("""
            {"lastSyncedTo":"2026-11-24T10:00:00.000000Z",
             "backfillCursorDate":"2026-08-24T14:00:00.000000Z",
             "backfillFloorDate":"2026-01-01T00:00:00.000000Z"}
            """));

    [Fact]
    public void Each_block_leaves_no_tenant_and_the_sessions_time_zone_to_the_rest_of_its_transaction()
    {
        fixture.SettingsAfterMove.Should().Be(("", MoveGoogleHealthStateToSlotsFixture.SessionTimeZone));
        fixture.SettingsAfterRestore.Should().Be(("", MoveGoogleHealthStateToSlotsFixture.SessionTimeZone));
    }

    [Fact]
    public async Task A_purged_position_leaves_nothing_behind() =>
        After(MoveGoogleHealthStateToSlotsFixture.Purged).Vendor.Should().Be(await fixture.CanonicalAsync("{}"));

    [Fact]
    public void Rows_of_other_connectors_and_rows_already_in_the_new_shape_are_unchanged()
    {
        foreach (var key in new[]
                 {
                     (Finished, "dexcom"), (MoveGoogleHealthStateToSlotsFixture.NewShape, GoogleHealth),
                     (MoveGoogleHealthStateToSlotsFixture.Empty, GoogleHealth),
                 })
            fixture.RowsAfter[key].Should().Be(fixture.RowsBefore[key], $"{key} is not moved");
    }

    [Fact]
    public void Every_row_is_still_there() =>
        fixture.RowsAfter.Keys.Should().BeEquivalentTo(fixture.RowsBefore.Keys);

    [Fact]
    public void A_second_run_changes_nothing()
    {
        fixture.RowsAfterSecondRun.Should().BeEquivalentTo(fixture.RowsAfter);
        fixture.WorkAfterSecondRun.Should().BeEquivalentTo(fixture.WorkAfter);
    }

    [Fact]
    public void The_runtime_role_reads_the_moved_state()
    {
        fixture.ResumeAsRead[Finished].Should().BeEquivalentTo(new Dictionary<string, DateTime>
        {
            [GoogleHealthState.LastSyncedTo] = new(2026, 11, 24, 10, 5, 37, 123, DateTimeKind.Utc),
            [GoogleHealthState.BackfillCursor] = new(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc),
            [GoogleHealthState.BackfillFloor] = new(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc),
        });
        Complete(Finished).Should().BeTrue();
        Complete(HalfFinished).Should().BeFalse();
        fixture.ChunkDaysAsRead[HalfFinished]!.GetValue<int>().Should().Be(7);

        fixture.WorkAsRead[HalfFinished].Should().Be(new GoogleHealthWork(
            new GoogleHealthSyncProgress(GoogleHealthSyncPhase.Queued, null, 0, 4, 0, WorkerOwned: true), null));
        fixture.WorkAsRead[Disconnecting].Should().Be(new GoogleHealthWork(
            new GoogleHealthSyncProgress(GoogleHealthSyncPhase.Reading, "heart-rate", 1, 3, 12),
            new GoogleHealthDisconnectRequest(
                MoveGoogleHealthStateToSlotsFixture.DisconnectRequest, MoveGoogleHealthStateToSlotsFixture.DisconnectSubject)));
        fixture.WorkAsRead[Finished].Should().Be(GoogleHealthWork.None);
    }

    [Fact]
    public void Down_gives_back_what_the_release_before_reads()
    {
        fixture.WorkTableExistsAfterDown.Should().BeFalse();

        foreach (var (tenant, health) in new[]
                 {
                     (Finished, MoveGoogleHealthStateToSlotsFixture.FinishedState),
                     (HalfFinished, MoveGoogleHealthStateToSlotsFixture.HalfFinishedState),
                 })
        {
            var restored = fixture.RowsAfterDown[(tenant, GoogleHealth)];
            restored.Resume.Should().BeNull();
            SameState(RuntimeState(restored), JsonNode.Parse(health)!.AsObject());
        }

        var halfFinished = JsonNode.Parse(fixture.RowsAfterDown[(HalfFinished, GoogleHealth)].Vendor!)!.AsObject();
        JsonNode.DeepEquals(halfFinished["googleHealthProgress"], JsonNode.Parse(MoveGoogleHealthStateToSlotsFixture.QueuedProgress))
            .Should().BeTrue();
        halfFinished["kept"]!.ToJsonString().Should().Be("""{"a":1}""");

        var disconnecting = JsonNode.Parse(fixture.RowsAfterDown[(Disconnecting, GoogleHealth)].Vendor!)!.AsObject();
        JsonNode.DeepEquals(disconnecting["googleHealthProgress"], JsonNode.Parse(MoveGoogleHealthStateToSlotsFixture.ScheduledProgress))
            .Should().BeTrue();
        disconnecting["googleHealthDisconnect"]!["RequestId"]!.GetValue<Guid>()
            .Should().Be(MoveGoogleHealthStateToSlotsFixture.DisconnectRequest);
        disconnecting["googleHealthDisconnect"]!["SubjectId"]!.GetValue<Guid>()
            .Should().Be(MoveGoogleHealthStateToSlotsFixture.DisconnectSubject);
        var firstWindow = RuntimeState(fixture.RowsAfterDown[(Disconnecting, GoogleHealth)]);
        firstWindow["backfillChunkDays"]!.GetValue<int>().Should().Be(4);
        firstWindow.ContainsKey("backfillCursorDate").Should().BeFalse();

        fixture.RowsAfterDown[(Finished, "dexcom")].Should().Be(fixture.RowsBefore[(Finished, "dexcom")]);
    }

    [Fact]
    public void Up_after_down_gives_the_same_new_shape()
    {
        fixture.RowsAfterUpAgain.Should().BeEquivalentTo(fixture.RowsAfter);
        fixture.WorkAfterUpAgain.Should().BeEquivalentTo(fixture.WorkAfter);
    }

    private bool Complete(Guid tenant)
    {
        var resume = fixture.ResumeAsRead[tenant];
        return GoogleHealthState.BackfillComplete(
            resume.TryGetValue(GoogleHealthState.BackfillCursor, out var cursor) ? cursor : null,
            resume.TryGetValue(GoogleHealthState.BackfillFloor, out var floor) ? floor : null);
    }

    /// <summary>The runtime state a row's <c>health</c> key holds, as the release before parsed it.</summary>
    private static JsonObject RuntimeState(MoveGoogleHealthStateToSlotsFixture.StoredRow row) =>
        JsonNode.Parse(JsonNode.Parse(row.Vendor!)!["health"]!["LastGuid"]!.GetValue<string>())!.AsObject();

    /// <summary>Equal keys, times equal as instants, every other value equal as JSON.</summary>
    private static void SameState(JsonObject restored, JsonObject seeded)
    {
        restored.Select(pair => pair.Key).Should().BeEquivalentTo(seeded.Select(pair => pair.Key));
        foreach (var (key, value) in seeded)
        {
            if (key is "lastSyncedTo" or "backfillCursorDate" or "backfillFloorDate")
                DateTimeOffset.Parse(restored[key]!.GetValue<string>(), CultureInfo.InvariantCulture)
                    .Should().Be(DateTimeOffset.Parse(value!.GetValue<string>(), CultureInfo.InvariantCulture), key);
            else
                JsonNode.DeepEquals(restored[key], value).Should().BeTrue(key);
        }
    }
}
